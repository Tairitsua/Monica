using Monica.Core.Results;
using Monica.DevOps.K8S.Exceptions;
using Monica.DevOps.K8S.Models;
using Test.Monica.DevOps.K8S.Support;
using Xunit;

namespace Test.Monica.DevOps.K8S.Services;

public sealed class K8SRestartServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartResources_WhenConfigurationChangesDuringDiscovery_ShouldUseOneSnapshotAndReturnActualTargets(bool namespaceWide)
    {
        var operations = new ControlledK8SOperations();
        var gate = new ControlledK8SCallGate();
        var firstDiscovery = true;
        var cancellationToken = TestContext.Current.CancellationToken;
        operations.Store.AfterRead = count =>
        {
            if (count == 1)
            {
                var changed = ControlledK8SOperations.Configuration("node-b");
                changed.SensitiveOperationNameRestrictions = ["different"];
                operations.Store.Update(changed);
            }
        };
        operations.BeforeResponse = async (_, token) =>
        {
            if (firstDiscovery)
            {
                firstDiscovery = false;
                await gate.PauseAsync(token);
            }
        };

        var startedAt = DateTimeOffset.UtcNow;
        var execution = namespaceWide
            ? operations.Restart.RestartNamespaceAsync(ControlledK8SOperations.NAMESPACE, cancellationToken)
            : operations.Restart.RestartResourcesAsync(
                ControlledK8SOperations.NAMESPACE, K8SResourceType.Service, ControlledK8SOperations.SelectedServices, cancellationToken);
        try
        {
            await gate.Entered.WaitAsync(cancellationToken);
            var changed = ControlledK8SOperations.Configuration("node-c");
            changed.NamespaceScope = ["another-namespace"];
            changed.SensitiveOperationNameRestrictions = ["never"];
            operations.Store.Update(changed);
            gate.Release();

            var result = await execution;
            Assert.Equal(1, operations.Store.ReadCount);
            Assert.All(operations.Calls, call =>
            {
                Assert.Equal("node-a", call.Node);
                Assert.Equal("user-node-a", call.UserName);
                Assert.Equal(new[] { ControlledK8SOperations.NAMESPACE }, call.NamespaceScope);
                Assert.Equal(new[] { "allowed", "api" }, call.Restrictions);
                Assert.Equal(cancellationToken, call.CancellationToken);
            });
            Assert.Equal(ControlledK8SOperations.NAMESPACE, result.Namespace);
            Assert.Equal(K8SResourceType.Service, result.ResourceType);
            Assert.Equal(namespaceWide ? "Namespace Services (Backing Workloads)" : "Selected Services (Backing Workloads)", result.ScopeName);
            Assert.Equal(new[] { "allowed-alias", "allowed-main" }, result.AffectedTargets.Order(StringComparer.Ordinal));
            var workload = Assert.Single(result.Workloads);
            Assert.Equal("Deployment/api-node-a", workload.DisplayName);
            Assert.Equal(3, workload.DesiredReplicas);
            var ignored = Assert.Single(result.IgnoredTargets);
            Assert.Equal("blocked", ignored.Name);
            Assert.Equal(K8SMessageCode.SensitiveRestrictionMismatch, ignored.ReasonCode);
            Assert.Equal(new[] { "allowed, api" }, ignored.ReasonArguments);
            Assert.Equal(new[] { "rollout restart deployment/'api-node-a' -n 'team-a'" }, result.ExecutedCommands);
            Assert.Equal(result.ExecutedCommands, operations.Calls.Where(call => call.IsMutation).Select(call => call.Command));
            Assert.InRange(result.CompletedAtUtc, startedAt, DateTimeOffset.UtcNow);
        }
        finally
        {
            gate.Release();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartResources_WhenConfigurationChangesAfterPreview_ShouldRevalidateCurrentScopeAndRestrictions(
        bool namespaceWide,
        bool denyNamespace)
    {
        var operations = new ControlledK8SOperations();
        var cancellationToken = TestContext.Current.CancellationToken;
        var response = namespaceWide
            ? await operations.Facade.GetNamespaceRestartPreviewAsync(ControlledK8SOperations.NAMESPACE, cancellationToken)
            : await operations.Facade.GetBatchRestartPreviewAsync(
                ControlledK8SOperations.NAMESPACE, K8SResourceType.Service, ControlledK8SOperations.SelectedServices, cancellationToken);
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Null(response.Message);
        var preview = Assert.IsType<K8SRestartPreview>(response.Data);
        Assert.True(preview.HasRestartableTargets);

        var changed = ControlledK8SOperations.Configuration("node-b");
        if (denyNamespace)
        {
            changed.NamespaceScope = ["another-namespace"];
        }
        else
        {
            changed.SensitiveOperationNameRestrictions = ["never"];
        }

        operations.Store.Update(changed);
        operations.Calls.Clear();
        var execution = namespaceWide
            ? await operations.Facade.RestartNamespaceAsync(ControlledK8SOperations.NAMESPACE, cancellationToken)
            : await operations.Facade.RestartResourcesAsync(
                ControlledK8SOperations.NAMESPACE, K8SResourceType.Service, ControlledK8SOperations.SelectedServices, cancellationToken);

        Assert.Equal(ResultStatus.BadRequest, execution.Status);
        Assert.Null(execution.Data);
        Assert.Contains(denyNamespace ? "GeneratedMessages:NamespaceOutsideScope" : "GeneratedMessages:NoRestartableTargetsRemain",
            execution.Message, StringComparison.Ordinal);
        Assert.Equal(2, operations.Store.ReadCount);
        Assert.DoesNotContain(operations.Calls, call => call.IsMutation);
        if (denyNamespace)
        {
            Assert.Empty(operations.Calls);
        }
        else
        {
            Assert.NotEmpty(operations.Calls);
            Assert.All(operations.Calls, call =>
            {
                Assert.Equal("node-b", call.Node);
                Assert.Equal(new[] { "never" }, call.Restrictions);
            });
        }
    }
}
