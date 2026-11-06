using Monica.Core.Results;
using Monica.DevOps.K8S.Exceptions;
using Monica.DevOps.K8S.Models;
using Test.Monica.DevOps.K8S.Support;
using Xunit;

namespace Test.Monica.DevOps.K8S.Services;

public sealed class K8SScaleServiceTests
{
    [Theory]
    [InlineData(false, K8SScaleOperation.ScaleDown)]
    [InlineData(false, K8SScaleOperation.ScaleUp)]
    [InlineData(true, K8SScaleOperation.ScaleDown)]
    [InlineData(true, K8SScaleOperation.ScaleUp)]
    public async Task ScaleResources_WhenConfigurationChangesDuringDiscoveryAndCommands_ShouldUseOneSnapshotAndReturnReplicaPlans(
        bool namespaceWide,
        K8SScaleOperation operation)
    {
        var operations = new ControlledK8SOperations();
        var discoveryGate = new ControlledK8SCallGate();
        var commandGate = new ControlledK8SCallGate();
        var firstDiscovery = true;
        var firstMutation = true;
        var cancellationToken = TestContext.Current.CancellationToken;
        operations.Store.AfterRead = count =>
        {
            if (count == 1)
            {
                operations.Store.Update(ControlledK8SOperations.Configuration("node-b"));
            }
        };
        operations.BeforeResponse = async (call, token) =>
        {
            if (firstDiscovery)
            {
                firstDiscovery = false;
                await discoveryGate.PauseAsync(token);
            }
            else if (call.IsMutation && firstMutation)
            {
                firstMutation = false;
                await commandGate.PauseAsync(token);
            }
        };

        var startedAt = DateTimeOffset.UtcNow;
        var execution = namespaceWide
            ? operations.Scale.ScaleNamespaceAsync(ControlledK8SOperations.NAMESPACE, operation, cancellationToken)
            : operations.Scale.ScaleResourcesAsync(
                ControlledK8SOperations.NAMESPACE, K8SResourceType.Service, ControlledK8SOperations.SelectedServices, operation, cancellationToken);
        try
        {
            await discoveryGate.Entered.WaitAsync(cancellationToken);
            var changed = ControlledK8SOperations.Configuration("node-c");
            changed.NamespaceScope = ["another-namespace"];
            changed.SensitiveOperationNameRestrictions = ["never"];
            operations.Store.Update(changed);
            discoveryGate.Release();
            await commandGate.Entered.WaitAsync(cancellationToken);
            operations.Store.Update(ControlledK8SOperations.Configuration("node-d"));
            commandGate.Release();

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
            Assert.Equal(operation, result.Operation);
            Assert.Equal(namespaceWide ? "Namespace Services (Backing Workloads)" : "Selected Services (Backing Workloads)", result.ScopeName);
            Assert.Equal(new[] { "allowed-alias", "allowed-main" }, result.AffectedTargets.Order(StringComparer.Ordinal));
            var workload = Assert.Single(result.Workloads);
            Assert.Equal("Deployment/api-node-a", workload.DisplayName);
            Assert.Equal(operation, workload.Operation);
            Assert.Equal(3, workload.CurrentReplicas);
            Assert.Equal(5, workload.PreviousReplicas);
            Assert.Equal(operation == K8SScaleOperation.ScaleDown ? 0 : 5, workload.TargetReplicas);
            var ignored = Assert.Single(result.IgnoredTargets);
            Assert.Equal("blocked", ignored.Name);
            Assert.Equal(K8SMessageCode.SensitiveRestrictionMismatch, ignored.ReasonCode);
            Assert.Equal(new[] { "allowed, api" }, ignored.ReasonArguments);
            string[] expectedCommands = operation == K8SScaleOperation.ScaleDown
                ? ["annotate deployment/'api-node-a' 'monica.devops/previous-replicas=3' --overwrite -n 'team-a'",
                    "scale deployment/'api-node-a' --replicas=0 -n 'team-a'"]
                : ["scale deployment/'api-node-a' --replicas=5 -n 'team-a'"];
            Assert.Equal(expectedCommands, result.ExecutedCommands);
            Assert.Equal(expectedCommands, workload.CommandPreview);
            Assert.Equal(result.ExecutedCommands, operations.Calls.Where(call => call.IsMutation).Select(call => call.Command));
            Assert.InRange(result.CompletedAtUtc, startedAt, DateTimeOffset.UtcNow);
        }
        finally
        {
            discoveryGate.Release();
            commandGate.Release();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScaleResources_WhenConfigurationChangesAfterPreview_ShouldRevalidateCurrentScopeAndRestrictions(
        bool namespaceWide,
        bool denyNamespace)
    {
        var operations = new ControlledK8SOperations();
        var cancellationToken = TestContext.Current.CancellationToken;
        var response = namespaceWide
            ? await operations.Facade.GetNamespaceScalePreviewAsync(ControlledK8SOperations.NAMESPACE, K8SScaleOperation.ScaleDown, cancellationToken)
            : await operations.Facade.GetBatchScalePreviewAsync(
                ControlledK8SOperations.NAMESPACE, K8SResourceType.Service, ControlledK8SOperations.SelectedServices,
                K8SScaleOperation.ScaleDown, cancellationToken);
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Null(response.Message);
        var preview = Assert.IsType<K8SScalePreview>(response.Data);
        Assert.True(preview.HasScalableTargets);

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
            ? await operations.Facade.ScaleNamespaceAsync(ControlledK8SOperations.NAMESPACE, K8SScaleOperation.ScaleDown, cancellationToken)
            : await operations.Facade.ScaleResourcesAsync(
                ControlledK8SOperations.NAMESPACE, K8SResourceType.Service, ControlledK8SOperations.SelectedServices,
                K8SScaleOperation.ScaleDown, cancellationToken);

        Assert.Equal(ResultStatus.BadRequest, execution.Status);
        Assert.Null(execution.Data);
        Assert.Contains(denyNamespace ? "GeneratedMessages:NamespaceOutsideScope" : "GeneratedMessages:NoScalableTargetsRemain",
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
