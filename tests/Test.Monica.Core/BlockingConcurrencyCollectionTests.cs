using System.Reflection;
using AwesomeAssertions;
using Test.Monica.Core.Execution;
using Test.Monica.Core.Modularity;
using Xunit;

namespace Test.Monica.Core;

/// <summary>
/// Serializes tests that deliberately occupy worker threads while preserving parallel execution for all other classes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BlockingConcurrencyCollection
{
    internal const string Name = "blocking-concurrency-probes";
}

public sealed class BlockingConcurrencyCollectionTests
{
    [Fact]
    public void BlockingProbeClasses_ShouldUseTheNonparallelCollection()
    {
        var definition = typeof(BlockingConcurrencyCollection)
            .GetCustomAttribute<CollectionDefinitionAttribute>();
        definition.Should().NotBeNull();
        definition!.Name.Should().Be(BlockingConcurrencyCollection.Name);
        definition.DisableParallelization.Should().BeTrue();

        Type[] blockingProbeClasses =
        [
            typeof(ExecutionPipelineCatalogTests),
            typeof(ModuleDiagnosticsFacadeTests),
            typeof(ModuleDiagnosticsSnapshotConcurrencyTests),
            typeof(ModuleDiagnosticsSnapshotLifecycleTests),
            typeof(ModuleDiagnosticsBlockingChainTests),
            typeof(ModuleInitMetricsTests),
            typeof(ModuleStartupWorkTests)
        ];

        foreach (var testClass in blockingProbeClasses)
        {
            var collection = testClass.GetCustomAttribute<CollectionAttribute>();
            collection.Should().NotBeNull($"{testClass.Name} uses deliberate blocking probes");
            collection!.Name.Should().Be(BlockingConcurrencyCollection.Name);
        }
    }
}
