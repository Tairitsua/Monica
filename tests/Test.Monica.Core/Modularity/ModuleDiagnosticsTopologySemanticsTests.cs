using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ModuleDiagnosticsTopologySemanticsTests
{
    [Fact]
    public void GetSnapshot_WhenThreeModulesFormAChain_ShouldUseExactLongestPathDepth()
    {
        using var host = CreateHost(monica =>
            monica.AddModule<DiagnosticsChainRootModule, DiagnosticsChainRootOption>());

        var snapshot = CreateSnapshot(host.Services);
        var root = ModuleKey.FromModuleType(typeof(DiagnosticsChainRootModule));
        var middle = ModuleKey.FromModuleType(typeof(DiagnosticsChainMiddleModule));
        var leaf = ModuleKey.FromModuleType(typeof(DiagnosticsChainLeafModule));
        ModuleDiagnosticsDependencyEdge[] expectedEdges =
        [
            new() { SourceModule = root, TargetModule = middle },
            new() { SourceModule = middle, TargetModule = leaf }
        ];

        snapshot.Modules.Should().HaveCount(3);
        snapshot.Topology.Edges.Should().BeEquivalentTo(expectedEdges);
        snapshot.Topology.TopologicalOrder.Should().Equal(leaf, middle, root);
        snapshot.Topology.MaximumDepth.Should().Be(2);
        Depth(snapshot, leaf).Should().Be(0);
        Depth(snapshot, middle).Should().Be(1);
        Depth(snapshot, root).Should().Be(2);
        snapshot.Modules.Single(module => module.ModuleKey == root).DirectDependencyCount.Should().Be(1);
    }

    [Fact]
    public void GetSnapshot_WhenModulesFormADiamond_ShouldKeepOnlyDirectEdgesAndLongestBranchDepth()
    {
        using var host = CreateHost(monica =>
            monica.AddModule<DiagnosticsDiamondRootModule, DiagnosticsDiamondRootOption>());

        var snapshot = CreateSnapshot(host.Services);
        var root = ModuleKey.FromModuleType(typeof(DiagnosticsDiamondRootModule));
        var left = ModuleKey.FromModuleType(typeof(DiagnosticsDiamondLeftModule));
        var right = ModuleKey.FromModuleType(typeof(DiagnosticsDiamondRightModule));
        var leaf = ModuleKey.FromModuleType(typeof(DiagnosticsDiamondLeafModule));
        ModuleDiagnosticsDependencyEdge[] expectedEdges =
        [
            new() { SourceModule = root, TargetModule = left },
            new() { SourceModule = root, TargetModule = right },
            new() { SourceModule = left, TargetModule = leaf },
            new() { SourceModule = right, TargetModule = leaf }
        ];

        snapshot.Modules.Should().HaveCount(4);
        snapshot.Topology.Edges.Should().BeEquivalentTo(expectedEdges);
        snapshot.Topology.Edges.Should().NotContain(edge =>
            edge.SourceModule == root && edge.TargetModule == leaf);
        snapshot.Topology.MaximumDepth.Should().Be(2);
        Depth(snapshot, leaf).Should().Be(0);
        Depth(snapshot, left).Should().Be(1);
        Depth(snapshot, right).Should().Be(1);
        Depth(snapshot, root).Should().Be(2);
    }

    private static IHost CreateHost(Action<IMonicaBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            configure(monica);
        });
        return builder.Build();
    }

    private static ModuleDiagnosticsSnapshot CreateSnapshot(IServiceProvider services) =>
        new ModuleDiagnosticsService(
                services.GetRequiredService<MonicaApplication>(),
                Options.Create(new ModuleSystemOption()),
                services.GetRequiredService<IHostEnvironment>())
            .GetSnapshot();

    private static int Depth(ModuleDiagnosticsSnapshot snapshot, ModuleKey key) =>
        snapshot.Modules.Single(module => module.ModuleKey == key).DependencyDepth;
}

internal sealed class DiagnosticsChainRootModule : MonicaModule<DiagnosticsChainRootOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DiagnosticsChainMiddleModule>();
    }
}

internal sealed class DiagnosticsChainRootOption : ModuleOptions<DiagnosticsChainRootModule>;

internal sealed class DiagnosticsChainMiddleModule : MonicaModule<DiagnosticsChainMiddleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DiagnosticsChainLeafModule>();
    }
}

internal sealed class DiagnosticsChainMiddleOption : ModuleOptions<DiagnosticsChainMiddleModule>;

internal sealed class DiagnosticsChainLeafModule : MonicaModule<DiagnosticsChainLeafOption>;

internal sealed class DiagnosticsChainLeafOption : ModuleOptions<DiagnosticsChainLeafModule>;

internal sealed class DiagnosticsDiamondRootModule : MonicaModule<DiagnosticsDiamondRootOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DiagnosticsDiamondLeftModule>();
        module.Require<DiagnosticsDiamondRightModule>();
    }
}

internal sealed class DiagnosticsDiamondRootOption : ModuleOptions<DiagnosticsDiamondRootModule>;

internal sealed class DiagnosticsDiamondLeftModule : MonicaModule<DiagnosticsDiamondLeftOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DiagnosticsDiamondLeafModule>();
    }
}

internal sealed class DiagnosticsDiamondLeftOption : ModuleOptions<DiagnosticsDiamondLeftModule>;

internal sealed class DiagnosticsDiamondRightModule : MonicaModule<DiagnosticsDiamondRightOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DiagnosticsDiamondLeafModule>();
    }
}

internal sealed class DiagnosticsDiamondRightOption : ModuleOptions<DiagnosticsDiamondRightModule>;

internal sealed class DiagnosticsDiamondLeafModule : MonicaModule<DiagnosticsDiamondLeafOption>;

internal sealed class DiagnosticsDiamondLeafOption : ModuleOptions<DiagnosticsDiamondLeafModule>;
