using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Request class
/// </summary>
public class RequestDtoBuildingBlock : BuildingBlock
{
    internal RequestDtoBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.RequestDto, catalog)
    {
    }

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new RequestDtoBuildingBlock(shape, catalog);
        unit.CheckMode();
        return unit;
    }
}
