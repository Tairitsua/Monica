using Monica.Core.Results;
using Monica.DevOps.K8S.Models;
using Test.Monica.DevOps.K8S.Support;
using Xunit;

namespace Test.Monica.DevOps.K8S.Services;

public sealed class K8SClusterServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListNamespaceInventory_WhenFacadeReadsEvidence_ShouldReturnWholeCaptureOrFailure(bool failPodRead)
    {
        var fixture = new K8SStateFixture();
        if (failPodRead)
            fixture.Provider.Handler = (arguments, _) => arguments.StartsWith("get pod ", StringComparison.Ordinal)
                ? Task.FromException<string>(new IOException("Pod evidence failed."))
                : Task.FromResult(fixture.Provider.DefaultOutput(arguments));

        var response = await fixture.Facade.ListNamespaceInventoryAsync("alpha", TestContext.Current.CancellationToken);

        Assert.Equal(failPodRead ? ResStatus.InternalError : ResStatus.Ok, response.Status);
        if (failPodRead)
        {
            Assert.Null(response.Data);
            Assert.Equal("Pod evidence failed.", response.Message);
        }
        else
        {
            Assert.Null(response.Message);
            var capture = Assert.IsType<K8SNamespaceInventoryResult>(response.Data);
            Assert.Equal("alpha", capture.Namespace);
            Assert.Equal(2, capture.Items.Count);
        }
    }

    [Fact]
    public async Task ListNamespaceInventory_WhenConfigurationChangesDuringDiscovery_ShouldUseOneSnapshotAndReadPodsOnce()
    {
        var operations = new ControlledK8SOperations();
        var cancellationToken = TestContext.Current.CancellationToken;
        operations.Store.AfterRead = count =>
        {
            if (count == 1) operations.Store.Update(ControlledK8SOperations.Configuration("node-b"));
        };

        var result = await operations.Cluster.ListNamespaceInventoryAsync(ControlledK8SOperations.NAMESPACE, cancellationToken);

        Assert.Equal(1, operations.Store.ReadCount);
        Assert.Equal(3, operations.Calls.Count);
        Assert.Single(operations.Calls, call => call.Command.StartsWith("get pod ", StringComparison.Ordinal));
        Assert.All(operations.Calls, call =>
        {
            Assert.Equal("node-a", call.Node);
            Assert.Equal(cancellationToken, call.CancellationToken);
            Assert.False(call.IsMutation);
        });
        var service = Assert.Single(result.Items, item => item.Name == "allowed-main");
        Assert.Equal("Deployment/api-node-a", Assert.Single(service.RelatedWorkloads).DisplayName);
        Assert.Equal(3, service.DesiredReplicas);
        Assert.Equal(3, service.ReadyReplicas);
        var workload = Assert.Single(result.Items, item => item.Name == "api-node-a");
        Assert.Equal(new[] { "allowed-alias", "allowed-main", "blocked" }, workload.RelatedServices.Select(item => item.Name));
        Assert.Equal("api-node-a-pod", Assert.Single(workload.RelatedPods).Name);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), workload.LastRestartedAtUtc);
        Assert.Equal(result.FetchedAtUtc, result.ForResourceType(K8SResourceType.Service).FetchedAtUtc);
    }

    [Fact]
    public async Task ListNamespaceInventory_WhenKindsShareServices_ShouldPreserveDistinctWorkloadAndPodEvidence()
    {
        var fixture = new K8SStateFixture();
        fixture.Provider.Handler = (arguments, _) => Task.FromResult(arguments.StartsWith("get service ", StringComparison.Ordinal)
            ? """
              {"items":[
                {"metadata":{"name":"web","namespace":"alpha"},"spec":{"type":"NodePort","selector":{"app":"api"},"ports":[{"port":80,"nodePort":30080}]}},
                {"metadata":{"name":"web-dapr","namespace":"alpha"},"spec":{"selector":{"app":"api"},"ports":[{"name":"dapr-http","port":3500}]}},
                {"metadata":{"name":"database","namespace":"alpha"},"spec":{"clusterIP":"None","selector":{"app":"db"}}},
                {"metadata":{"name":"external","namespace":"alpha"},"spec":{"type":"ExternalName"}}
              ]}
              """
            : arguments.StartsWith("get deployment", StringComparison.Ordinal)
                ? """
                  {"items":[
                    {"kind":"Deployment","metadata":{"name":"api","namespace":"alpha"},"spec":{"replicas":3,"template":{"metadata":{"labels":{"app":"api"}},"spec":{"containers":[{"image":"api:v2"}]}}},"status":{"readyReplicas":2}},
                    {"kind":"Deployment","metadata":{"name":"idle","namespace":"alpha"},"spec":{"replicas":0},"status":{"readyReplicas":0}},
                    {"kind":"StatefulSet","metadata":{"name":"db","namespace":"alpha"},"spec":{"replicas":1,"template":{"metadata":{"labels":{"app":"db"}}}},"status":{"readyReplicas":1}},
                    {"kind":"DaemonSet","metadata":{"name":"agent","namespace":"alpha"},"status":{"desiredNumberScheduled":2,"numberReady":1}}
                  ]}
                  """
                : """
                  {"items":[
                    {"metadata":{"name":"api-pod","namespace":"alpha","ownerReferences":[{"kind":"ReplicaSet","name":"api-123","controller":true}]},"spec":{},"status":{"phase":"Running","startTime":"2026-01-02T03:04:05Z","initContainerStatuses":[{"restartCount":2}],"containerStatuses":[{"ready":true,"restartCount":4}]}},
                    {"metadata":{"name":"db-0","namespace":"alpha","ownerReferences":[{"kind":"StatefulSet","name":"db","controller":true}]},"spec":{},"status":{"phase":"Running","startTime":"2026-02-01T00:00:00Z","containerStatuses":[{"ready":true,"restartCount":0}]}},
                    {"metadata":{"name":"unrelated-pod","namespace":"alpha"},"spec":{},"status":{"phase":"Running","containerStatuses":[{"ready":true,"restartCount":99}]}}
                  ]}
                  """);

        var result = await fixture.Cluster.ListNamespaceInventoryAsync("alpha", TestContext.Current.CancellationToken);

        Assert.Equal(8, result.Items.Count);
        Assert.Equal(4, result.ForResourceType(K8SResourceType.Service).Items.Count);
        Assert.Equal(2, result.ForResourceType(K8SResourceType.Deployment).Items.Count);
        Assert.Single(result.ForResourceType(K8SResourceType.StatefulSet).Items);
        var daemon = Assert.Single(result.ForResourceType(K8SResourceType.DaemonSet).Items);
        Assert.Equal(2, daemon.DesiredReplicas);
        Assert.Equal(1, daemon.ReadyReplicas);
        Assert.Empty(daemon.RelatedServices);
        var api = Assert.Single(result.Items, item => item.ResourceType == K8SResourceType.Deployment && item.Name == "api");
        Assert.Equal(new[] { "web", "web-dapr" }, api.RelatedServices.Select(item => item.Name));
        Assert.Equal(6, api.RestartCount);
        Assert.True(api.HasPodEvidence);
        var web = Assert.Single(result.Items, item => item.Name == "web");
        Assert.Equal(3, web.DesiredReplicas);
        Assert.Equal(2, web.ReadyReplicas);
        Assert.Equal(6, web.RestartCount);
        Assert.Equal("api:v2", Assert.Single(web.Images));
        Assert.Equal(api.LastRestartedAtUtc, web.LastRestartedAtUtc);
        Assert.Equal(30080, Assert.Single(web.Ports).NodePort);
        var external = Assert.Single(result.Items, item => item.Name == "external");
        Assert.Empty(external.RelatedWorkloads);
        Assert.False(external.HasPodEvidence);
        Assert.Null(external.LastRestartedAtUtc);
        var idle = Assert.Single(result.Items, item => item.Name == "idle");
        Assert.False(idle.HasPodEvidence);
        Assert.Equal(0, idle.DesiredReplicas);
        Assert.Equal(3, fixture.Provider.Commands.Count);
    }

    [Theory]
    [InlineData(K8SResourceType.Service)]
    [InlineData(K8SResourceType.Deployment)]
    public async Task DescribeResource_WhenConfigurationChangesDuringDiscovery_ShouldReturnDetailsFromOneSnapshot(K8SResourceType resourceType)
    {
        var operations = new ControlledK8SOperations();
        var gate = new ControlledK8SCallGate();
        var firstDiscovery = true;
        var cancellationToken = TestContext.Current.CancellationToken;
        operations.Store.AfterRead = count =>
        {
            if (count == 1)
            {
                operations.Store.Update(ControlledK8SOperations.Configuration("node-b"));
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

        var resourceName = resourceType == K8SResourceType.Service ? "allowed-main" : "api-node-a";
        var execution = operations.Cluster.DescribeResourceAsync(
            ControlledK8SOperations.NAMESPACE, resourceType, resourceName, cancellationToken);
        try
        {
            await gate.Entered.WaitAsync(cancellationToken);
            operations.Store.Update(ControlledK8SOperations.Configuration("node-c"));
            gate.Release();

            var details = await execution;
            Assert.Equal(1, operations.Store.ReadCount);
            Assert.All(operations.Calls, call =>
            {
                Assert.Equal("node-a", call.Node);
                Assert.Equal("user-node-a", call.UserName);
                Assert.Equal(new[] { ControlledK8SOperations.NAMESPACE }, call.NamespaceScope);
                Assert.Equal(new[] { "allowed", "api" }, call.Restrictions);
                Assert.Equal(cancellationToken, call.CancellationToken);
            });
            Assert.Equal(resourceName, details.Summary.Name);
            Assert.Equal(ControlledK8SOperations.NAMESPACE, details.Summary.Namespace);
            Assert.Equal(resourceType, details.Summary.ResourceType);
            Assert.Equal("description from node-a", details.DescriptionText);
            Assert.Equal("Deployment/api-node-a", Assert.Single(details.RelatedWorkloads).DisplayName);
            var pod = Assert.Single(details.RelatedPods);
            Assert.Equal("api-node-a-pod", pod.Name);
            Assert.Equal("node-a", pod.NodeName);
            Assert.Equal("api-node-a", pod.WorkloadName);
            Assert.Equal("Deployment", pod.WorkloadKind);
            Assert.True(pod.IsReady);
            Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), details.Summary.LastRestartedAtUtc);
            if (resourceType == K8SResourceType.Service)
            {
                Assert.Equal("node-a", details.Summary.ClusterIp);
            }

            Assert.Single(operations.Calls, call => call.Command.StartsWith("describe ", StringComparison.Ordinal));
            Assert.Contains(operations.Calls, call => call.Command.StartsWith("get pod ", StringComparison.Ordinal));
            Assert.DoesNotContain(operations.Calls, call => call.IsMutation);
        }
        finally
        {
            gate.Release();
        }
    }
}
