using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Services.Support;
using Monica.JobScheduler;
using Monica.JobScheduler.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Background scheduled jobs
/// </summary>
public class RecurringJobBuildingBlock : BuildingBlock
{
    internal RecurringJobBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.RecurringJob, catalog, JobSchedulerExecutionPoints.RecurringAttempt)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;
    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Prefix = "Worker"
        };
    }

    internal static BuildingBlock Create(BusinessTypeShape shape, BuildingBlockCatalog catalog)
    {
        var unit = new RecurringJobBuildingBlock(shape, catalog);
        unit.CheckMode();
        return unit;
    }
}
