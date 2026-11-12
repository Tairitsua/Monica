using Microsoft.Extensions.Hosting;
using Monica.Core.Execution;
using Monica.Core.HostedService;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes a host-managed service and its constructor dependencies.
/// </summary>
public sealed class HostedServiceBuildingBlock : BuildingBlock
{
    private HostedServiceBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.HostedService, catalog, GetExecutionPoints(shape))
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new HostedServiceBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.InitializeMethods<IHostedService>();
        return unit;
    }

    private static ExecutionPoint[] GetExecutionPoints(BusinessTypeShape shape)
    {
        if (!shape.IsAssignableTo(typeof(MoHostedService))
            && !shape.IsAssignableTo(typeof(MoBackgroundService)))
        {
            return [];
        }

        return
        [
            HostedServiceExecutionPoints.Start,
            HostedServiceExecutionPoints.Stop,
            HostedServiceExecutionPoints.WorkItem
        ];
    }
}
