using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.DevOps.K8S.Abstractions;
using Monica.DevOps.K8S.Facades;
using Monica.DevOps.K8S.Models;
using Monica.DevOps.K8S.Services;
using Monica.DevOps.K8S.Services.Support;
using Monica.DevOps.Localization;
using Monica.Modules;
using Monica.Testing.Localization;

namespace Test.Monica.DevOps.K8S.Support;

internal sealed class K8SStateFixture
{
    internal K8SStateFixture()
    {
        Store = new(new K8SRuntimeConfigStore(Options.Create(new ModuleK8SOption
        {
            RuntimeConfig = new() { ExecutionNode = "fixture-node", UserName = "root", Password = "fixture-secret" }
        })));
        var discovery = new K8SResourceDiscoveryService(Provider);
        var localizer = new EchoStringLocalizer<K8SResource>();
        Cluster = new K8SClusterService(Store, discovery, Provider, NullLogger<K8SClusterService>.Instance);
        Facade = new(Cluster,
            new K8SRestartService(discovery, Store, Provider), new K8SScaleService(discovery, Store, Provider),
            new K8SMessageLocalizer(localizer), localizer, NullLogger<K8SFacade>.Instance);
    }

    internal ControlledK8SProvider Provider { get; } = new();
    internal ControlledK8SConfigStore Store { get; }
    internal K8SFacade Facade { get; }
    internal K8SClusterService Cluster { get; }
}

internal sealed class ControlledK8SProvider : IK8SProvider
{
    internal List<string> Commands { get; } = [];
    internal string WorkloadJson { get; set; } = """
        {"items":[
          {"kind":"Deployment","metadata":{"name":"api","namespace":"alpha"},"spec":{"replicas":2,"template":{"metadata":{"labels":{"app":"api"}},"spec":{"containers":[{"image":"app:v1"}]}}},"status":{"readyReplicas":2}},
          {"kind":"Deployment","metadata":{"name":"worker","namespace":"alpha"},"spec":{"replicas":1,"template":{"metadata":{"labels":{"app":"worker"}},"spec":{"containers":[{"image":"worker:v1"}]}}},"status":{"readyReplicas":1}}
        ]}
        """;
    internal Func<string, CancellationToken, Task<string>>? Handler { get; set; }

    public Task<string> ExecuteKubectlAsync(K8SRuntimeConfig runtimeConfig, string kubectlArguments,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(kubectlArguments);
        return Handler?.Invoke(kubectlArguments, cancellationToken) ?? Task.FromResult(DefaultOutput(kubectlArguments));
    }

    internal string DefaultOutput(string arguments) => arguments.StartsWith("get namespaces", StringComparison.Ordinal)
        ? """{"items":[{"metadata":{"name":"alpha"}},{"metadata":{"name":"beta"}}]}"""
        : arguments.StartsWith("get deployment", StringComparison.Ordinal) ? WorkloadJson
        : arguments.StartsWith("describe", StringComparison.Ordinal) ? "Exact description"
        : """{"items":[]}""";
}

internal sealed class ControlledK8SConfigStore(K8SRuntimeConfigStore store) : IK8SRuntimeConfigStore
{
    internal bool FailRead { get; set; }
    public K8SRuntimeConfig GetCurrent() => FailRead ? throw new IOException("Controlled configuration reload failure.") : store.GetCurrent();
    public void Update(K8SRuntimeConfig runtimeConfig) => store.Update(runtimeConfig);
}
