using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Services.Support;
using Monica.JobScheduler;
using Monica.JobScheduler.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Background triggered tasks
/// </summary>
public class TriggeredJobBuildingBlock : BuildingBlock
{
    internal TriggeredJobBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.TriggeredJob, catalog, JobSchedulerExecutionPoints.TriggeredAttempt)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    /// <summary>
    /// Gets or sets the argument type accepted by the triggered job.
    /// </summary>
    public Type? JobArgsType { get; set; }

    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Prefix = "Job"
        };
    }

    internal static BuildingBlock Create(
        BusinessTypeShape shape,
        BuildingBlockCatalog catalog,
        Type jobArgsType)
    {
        var unit = new TriggeredJobBuildingBlock(shape, catalog);
        unit.CheckMode();
        unit.JobArgsType = jobArgsType;
        return unit;
    }
}
