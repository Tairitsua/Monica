using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Core;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.BuildingBlocks.Abstractions;
using Xunit;

namespace Test.Monica.BuildingBlocks;

public sealed class BuildingBlocksGenericHostTests
{
    [Fact]
    public void Build_WhenUsingGenericHost_ShouldRetainBuildingBlockDiscoveryServices()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddBuildingBlocks();
        });

        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();

        host.Services.GetRequiredService<IBuildingBlockCatalog>().Should().NotBeNull();
        application.Modules.RuntimeSnapshots.Should().ContainSingle(snapshot =>
            snapshot.ModuleType == typeof(ModuleBuildingBlocks)
            && snapshot.IsWebModule
            && !snapshot.RequiresWebHost);
    }

    [Fact]
    public void Composition_ShouldEnrichReloadBehaviorBeforeCatalogResolution()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(BuildingBlockCatalogTests).Assembly));
            monica.AddConfiguration();
            monica.AddBuildingBlocks();
        });

        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<IConfigurationDefinitionRegistry>();

        // Deliberately resolves no IBuildingBlockCatalog: reload-behavior inference must have enriched
        // the definition registry during composition so metadata publication never observes the
        // scanner's unresolved defaults.
        registry.TryGet("Test.ReloadableOptions", out var definition).Should().BeTrue();
        definition!.ReloadBehaviorObservationKind.Should().Be(ConfigurationReloadBehaviorObservationKind.Inferred);
        definition.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.OnlineReloadable);
    }

    [Fact]
    public async Task Resolve_Concurrently_ShouldPublishOneFullyConnectedCatalog()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(BuildingBlockCatalogTests).Assembly));
            monica.AddBuildingBlocks();
        });

        using var host = builder.Build();
        var resolutions = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(
                () => host.Services.GetRequiredService<IBuildingBlockCatalog>(),
                TestContext.Current.CancellationToken))
            .ToArray();
        var catalogs = await Task.WhenAll(resolutions);

        catalogs.Should().OnlyContain(catalog => ReferenceEquals(catalog, catalogs[0]));
        catalogs[0]
            .FindByFullName(typeof(BuildingBlockCatalogTests.CompleteDomainService).FullName)!
            .Dependencies.Should().ContainSingle(dependency =>
                dependency.ClrType == typeof(BuildingBlockCatalogTests.DependencyDomainService));
    }
}
