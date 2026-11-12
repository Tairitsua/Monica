using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Domain services
/// </summary>
public class DomainServiceBuildingBlock : BuildingBlock
{
    internal DomainServiceBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.DomainService, catalog)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;
    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Prefix = "Domain"
        };
    }

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new DomainServiceBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.InitializeMethods<DomainService>();
        return unit;
    }
}
