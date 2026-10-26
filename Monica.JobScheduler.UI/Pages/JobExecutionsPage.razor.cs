using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.JobScheduler.UI.Localization;

namespace Monica.JobScheduler.UI.Pages;

/// <summary>Routes an execution ledger whose filters, native dialogs, and lifetime belong to its query identity.</summary>
public partial class JobExecutionsPage
{
    /// <summary>Gets the route for the execution ledger.</summary>
    public const string PAGE_URL = "/job-scheduler/executions";

    [Inject]
    private IStringLocalizer<JobSchedulerResource> L { get; set; } = null!;

    /// <summary>
    /// Gets or sets the optional comma-separated execution-state filter. Query changes retire the previous owner.
    /// </summary>
    [SupplyParameterFromQuery(Name = "states")]
    public string? InitialStates { get; set; }

    /// <summary>
    /// Gets or sets the exact, opaque execution identifier to search and inspect initially.
    /// The ledger retains its identifier substring-search semantics; the inspector loads this unnormalized ID.
    /// </summary>
    [SupplyParameterFromQuery(Name = "instanceId")]
    public string? InitialInstanceId { get; set; }

    /// <summary>Gets the exact owner scope carried by a cross-page drilldown.</summary>
    [SupplyParameterFromQuery(Name = "owner")] public string? InitialOwner { get; set; }
    /// <summary>Gets the exact job scope carried by a cross-page drilldown.</summary>
    [SupplyParameterFromQuery(Name = "job")] public string? InitialJob { get; set; }
    /// <summary>Gets the inclusive UTC creation bound as an exact offset-qualified ISO instant or Unix milliseconds.</summary>
    [SupplyParameterFromQuery(Name = "from")] public string? InitialFrom { get; set; }
    /// <summary>Gets the exclusive UTC creation bound as an exact offset-qualified ISO instant or Unix milliseconds.</summary>
    [SupplyParameterFromQuery(Name = "to")] public string? InitialTo { get; set; }
    /// <summary>Gets the route-held case-insensitive identifier search.</summary>
    [SupplyParameterFromQuery(Name = "q")] public string? InitialSearch { get; set; }
    /// <summary>Gets the route-held rolling range preset.</summary>
    [SupplyParameterFromQuery(Name = "range")] public string? InitialRange { get; set; }
}
