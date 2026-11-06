using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder;
using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a startup seeder and its constructor dependencies.
/// </summary>
public sealed class SeederBuildingBlock : BuildingBlock
{
    private SeederBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.Seeder, catalog, SeederExecutionPoints.Run)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new SeederBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.InitializeMethods<ISeeder>();
        return unit;
    }
}
