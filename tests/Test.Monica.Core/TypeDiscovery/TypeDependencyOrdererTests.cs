using AwesomeAssertions;
using Monica.Core;
using Monica.Core.TypeDiscovery.Services;
using Monica.Core.TypeDiscovery.Services.Support;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Core.TypeDiscovery;

public sealed class TypeDependencyOrdererTests
{
    [Fact]
    public void OrderDependencyFirst_WhenSelectedAssembliesDependOnEachOther_ShouldOrderDependenciesThenTypeIdentities()
    {
        var orderer = new TypeDependencyOrderer(
            [
                typeof(TypeDependencyOrdererTests).Assembly,
                typeof(MonicaTestApplication).Assembly,
                typeof(DomainTypeFinder).Assembly
            ]);

        var types = orderer.OrderDependencyFirst(
            [
                typeof(ZStableOrderType),
                typeof(MonicaTestApplication),
                typeof(DomainTypeFinder),
                typeof(AStableOrderType),
                typeof(DomainTypeFinder)
            ]);

        types.Should().Equal(
            typeof(DomainTypeFinder),
            typeof(MonicaTestApplication),
            typeof(AStableOrderType),
            typeof(ZStableOrderType));
    }

    [Fact]
    public void OrderDependencyFirst_WhenOnlyNonSelectedDependenciesDiffer_ShouldUseAssemblyIdentityTieOrder()
    {
        var graph = CreateGraph(
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["Selected.A"] = ["Z.NonSelected"],
                ["Selected.B"] = [],
                ["Z.NonSelected"] = []
            });

        graph.OrderDependencyFirst(["Selected.A", "Selected.B"])
            .Should().Equal("Selected.A", "Selected.B");
    }

    [Fact]
    public void OrderDependencyFirst_WhenSelectedAssembliesAreConnectedThroughNonSelectedAssembly_ShouldRetainDependency()
    {
        var graph = CreateGraph(
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["Selected.Adapter"] = ["NonSelected.Bridge"],
                ["NonSelected.Bridge"] = ["Selected.Domain"],
                ["Selected.Domain"] = []
            });

        graph.OrderDependencyFirst(["Selected.Adapter", "Selected.Domain"])
            .Should().Equal("Selected.Domain", "Selected.Adapter");
    }

    [Fact]
    public void OrderDependencyFirst_WhenUnrelatedNonSelectedAssembliesContainCycle_ShouldIgnoreIt()
    {
        var graph = CreateGraph(
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["Selected.A"] = [],
                ["Selected.B"] = [],
                ["NonSelected.Left"] = ["NonSelected.Right"],
                ["NonSelected.Right"] = ["NonSelected.Left"]
            });

        graph.OrderDependencyFirst(["Selected.A", "Selected.B"])
            .Should().Equal("Selected.A", "Selected.B");
    }

    [Fact]
    public void OrderDependencyFirst_WhenSelectedGraphContainsCycle_ShouldSeparateParticipantsFromBlockedConsumers()
    {
        var graph = CreateGraph(
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["Domain"] = ["Adapter"],
                ["Adapter"] = ["Domain"],
                ["Consumer"] = ["Domain"],
                ["Independent"] = []
            });

        var exception = Assert.Throws<InvalidOperationException>(
            () => graph.OrderDependencyFirst(["Domain", "Adapter", "Consumer", "Independent"]));

        exception.Message.Should().Be(
            "Type dependency order cannot be resolved because the selected assembly dependency graph contains a cycle. " +
            "Cycle participants: Adapter, Domain. Blocked selected assemblies: Consumer.");
    }

    [Fact]
    public void OrderDependencyFirst_WhenSelectedAssemblyIsOutsideConfiguredGraph_ShouldFailClearly()
    {
        var graph = CreateGraph(
            new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["Configured"] = []
            });

        var exception = Assert.Throws<InvalidOperationException>(
            () => graph.OrderDependencyFirst(["Outside"]));

        exception.Message.Should().Be(
            "Selected assembly 'Outside' is absent from the configured type-discovery assembly graph.");
    }

    [Fact]
    public void TypeDependencyOrderer_ShouldBeCachedPerHostAndRebuiltWithTypeDiscovery()
    {
        using var application = new MonicaApplication();
        application.ConfigureTypeDiscovery(options => options
            .ExcludeDefault()
            .Add(typeof(TypeDependencyOrdererTests).Assembly));

        var initialOrderer = application.TypeDependencyOrderer;

        application.TypeDependencyOrderer.Should().BeSameAs(initialOrderer);

        application.ConfigureTypeDiscovery(options => options
            .ExcludeDefault()
            .Add(typeof(DomainTypeFinder).Assembly));

        application.TypeDependencyOrderer.Should().NotBeSameAs(initialOrderer);
    }

    private static TypeDependencyGraph CreateGraph(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependenciesByAssembly)
    {
        return new TypeDependencyGraph(dependenciesByAssembly);
    }

    private sealed class AStableOrderType;

    private sealed class ZStableOrderType;
}
