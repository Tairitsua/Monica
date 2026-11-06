using Microsoft.AspNetCore.Mvc;
using Monica.Core.Execution;
using Monica.Core.Execution.Mvc;
using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes an ASP.NET Core MVC controller. Generated mediated controllers remain discoverable but do not advertise
/// the direct MVC execution point.
/// </summary>
public sealed class HttpApiBuildingBlock : BuildingBlock
{
    private HttpApiBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.HttpApi, catalog, GetExecutionPoints(shape))
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new HttpApiBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.InitializeMethods<ControllerBase>();
        return unit;
    }

    private static ExecutionPoint[] GetExecutionPoints(BusinessTypeShape shape)
    {
        return shape.HasAttribute(typeof(MediatedControllerAttribute), inherit: false)
            ? []
            : [MvcExecutionPoints.Action];
    }
}
