using Monica.Core.Execution.Mvc;
using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// CRUD application services that participate in automatic controller generation (implement
/// <see cref="ICrudApplicationService"/>).
/// </summary>
/// <remarks>
/// These services also derive from <see cref="ApplicationService"/>, but they are modelled as their own building-block
/// type so their naming convention is owned by the building-block system rather than the web layer. The web layer
/// (Monica.WebApi) holds no naming rule for them; configure the convention through
/// <c>ModuleBuildingBlocksOption.Naming.Rules[BuildingBlockKind.CrudApplicationService]</c>.
/// </remarks>
public class CrudApplicationServiceBuildingBlock : BuildingBlock
{
    internal CrudApplicationServiceBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.CrudApplicationService, catalog, MvcExecutionPoints.Action)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new CrudApplicationServiceBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.InitializeMethods<ApplicationService>();
        return unit;
    }
}
