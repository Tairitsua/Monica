using System.Collections.Immutable;
using Monica.Core.Modularity.Models;

namespace Monica.Core.Modularity.Diagnostics.Models;

/// <summary>
/// Represents one immutable, internally consistent observation of a host's module composition.
/// </summary>
public sealed record ModuleDiagnosticsSnapshot
{
    /// <summary>Gets the current portable diagnostics schema version.</summary>
    public const int CURRENT_SCHEMA_VERSION = 4;

    /// <summary>Gets the schema version used by this snapshot.</summary>
    public int SchemaVersion { get; init; } = CURRENT_SCHEMA_VERSION;

    /// <summary>Gets the opaque identity of this host-owned composition.</summary>
    public required string CompositionId { get; init; }

    /// <summary>Gets the monotonic revision of this composition observation.</summary>
    public long Revision { get; init; }

    /// <summary>Gets the UTC time at which the composition timeline started.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Gets the UTC time at which this snapshot was captured.</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>
    /// Gets whether composition and every scheduled startup work item have reached a terminal state.
    /// </summary>
    public bool IsFinal { get; init; }

    /// <summary>Gets the terminal structural outcome, or <see langword="null"/> while composition is live.</summary>
    public ModuleCompositionOutcome? Outcome { get; init; }

    /// <summary>Gets bounded host context useful for comparing startup observations.</summary>
    public ModuleDiagnosticsHostContext Host { get; init; } = new();

    /// <summary>Gets exact counts, timings, and configured performance-budget evaluations.</summary>
    public ModuleDiagnosticsSummary Summary { get; init; } = new();

    /// <summary>Gets normalized timeline spans in occurrence order.</summary>
    public ImmutableArray<ModuleDiagnosticsTraceSpan> TraceSpans { get; init; } = [];

    /// <summary>Gets type-discovery stages, counts, and query summaries.</summary>
    public TypeDiscoveryDiagnostics TypeDiscovery { get; init; } = new();

    /// <summary>Gets one record for every declared module.</summary>
    public ImmutableArray<ModuleDiagnosticsModule> Modules { get; init; } = [];

    /// <summary>Gets the direct dependency topology compiled for this host.</summary>
    public ModuleDiagnosticsTopology Topology { get; init; } = new();

    /// <summary>Gets every causal startup-work segment that contributed to a blocking barrier.</summary>
    public ImmutableArray<ModuleBlockingChainSegment> BlockingChain { get; init; } = [];

    /// <summary>Gets structured findings whose display text is owned by the consumer.</summary>
    public ImmutableArray<ModuleDiagnosticFinding> Findings { get; init; } = [];
}

/// <summary>Defines terminal structural outcomes for module composition.</summary>
public enum ModuleCompositionOutcome
{
    /// <summary>Composition completed without warnings or failures.</summary>
    Succeeded,

    /// <summary>Composition completed but one or more diagnostic warnings were recorded.</summary>
    Degraded,

    /// <summary>Composition or required startup work failed.</summary>
    Failed
}

/// <summary>Contains bounded host metadata safe for the live diagnostics boundary.</summary>
public sealed record ModuleDiagnosticsHostContext
{
    /// <summary>Gets the host's frozen Application:DebugMode policy. This does not grant access to diagnostics.</summary>
    public bool DebugMode { get; init; }

    /// <summary>Gets the configured application display name.</summary>
    public string? ApplicationName { get; init; }

    /// <summary>Gets the configured application version.</summary>
    public string? ApplicationVersion { get; init; }

    /// <summary>Gets the hosting environment name.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Gets the concrete host-builder kind.</summary>
    public string? HostKind { get; init; }
}

/// <summary>Contains non-overlapping startup totals and independently labelled aggregate measurements.</summary>
public sealed record ModuleDiagnosticsSummary
{
    /// <summary>Gets the number of declared modules.</summary>
    public int ModuleCount { get; init; }

    /// <summary>Gets the number of active modules.</summary>
    public int ActiveModuleCount { get; init; }

    /// <summary>Gets the number of intentionally disabled module declarations.</summary>
    public int DisabledModuleCount { get; init; }

    /// <summary>Gets the number of startup-work items currently queued or running.</summary>
    public int ActiveStartupWorkCount { get; init; }

    /// <summary>Gets the number of captured registration or startup-work failures.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Gets the exact end-to-end composition duration in milliseconds.</summary>
    public double TotalCompositionDurationMs { get; init; }

    /// <summary>
    /// Gets the explicitly tracked monotonic application startup duration through <c>ApplicationStarted</c>, or
    /// <see langword="null"/> when tracking is disabled or the application is not yet ready.
    /// </summary>
    public double? ApplicationStartupDurationMs { get; init; }

    /// <summary>Gets the exact service-registration duration in milliseconds.</summary>
    public double ServiceRegistrationDurationMs { get; init; }

    /// <summary>Gets the aggregate duration of the typed discovery stages in milliseconds.</summary>
    public double TypeDiscoveryDurationMs { get; init; }

    /// <summary>Gets aggregate serial blocking time imposed by startup-work barriers.</summary>
    public double AggregateBarrierWaitDurationMs { get; init; }

    /// <summary>Gets the longest completed serial module callback duration.</summary>
    public double LongestModuleCallbackDurationMs { get; init; }

    /// <summary>Gets the longest startup-work queue duration.</summary>
    public double LongestStartupQueueDurationMs { get; init; }

    /// <summary>Gets evaluations only for budgets explicitly configured by the host.</summary>
    public ImmutableArray<ModulePerformanceBudgetEvaluation> PerformanceBudgets { get; init; } = [];
}

/// <summary>Identifies a module startup measurement that can have an optional host budget.</summary>
public enum ModulePerformanceBudgetKind
{
    /// <summary>End-to-end module composition.</summary>
    TotalComposition,

    /// <summary>Service registration through the completed registration milestone.</summary>
    ServiceRegistration,

    /// <summary>Aggregate typed type-discovery stages.</summary>
    TypeDiscovery,

    /// <summary>Aggregate serial startup-work barrier waiting.</summary>
    AggregateBarrierWait,

    /// <summary>Longest individual serial module callback.</summary>
    LongestModuleCallback,

    /// <summary>Longest individual startup-work queue wait.</summary>
    LongestStartupQueue
}

/// <summary>Compares an observed startup measurement with one explicitly configured budget.</summary>
public sealed record ModulePerformanceBudgetEvaluation
{
    /// <summary>Gets the measured quantity.</summary>
    public required ModulePerformanceBudgetKind Kind { get; init; }

    /// <summary>Gets the observed duration in milliseconds.</summary>
    public double ActualDurationMs { get; init; }

    /// <summary>Gets the configured upper limit in milliseconds.</summary>
    public double LimitDurationMs { get; init; }

    /// <summary>Gets actual divided by limit; values above one exceed the budget.</summary>
    public double Utilization => LimitDurationMs <= 0 ? 0 : ActualDurationMs / LimitDurationMs;

    /// <summary>Gets whether the observed value exceeds its configured budget.</summary>
    public bool IsExceeded => ActualDurationMs > LimitDurationMs;
}

/// <summary>Describes one declared module without exposing its live option instance.</summary>
public sealed record ModuleDiagnosticsModule
{
    /// <summary>Gets the host-local diagnostic module key.</summary>
    public required ModuleKey ModuleKey { get; init; }

    /// <summary>Gets the short CLR type name.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets the fully qualified CLR type name.</summary>
    public required string FullTypeName { get; init; }

    /// <summary>Gets the defining assembly's simple name.</summary>
    public required string AssemblyName { get; init; }

    /// <summary>Gets the dependency-first registration order, or <see langword="null"/> when disabled.</summary>
    public int? RegistrationOrder { get; init; }

    /// <summary>Gets the last lifecycle phase observed for this module.</summary>
    public ModulePhase Phase { get; init; }

    /// <summary>Gets whether the module participates in the executable graph.</summary>
    public bool IsActive { get; init; }

    /// <summary>Gets whether this module owns UI composition.</summary>
    public bool IsUiModule { get; init; }

    /// <summary>Gets whether this module contributes ASP.NET Core middleware or endpoints.</summary>
    public bool IsWebModule { get; init; }

    /// <summary>Gets whether this module requires an ASP.NET Core host adapter.</summary>
    public bool RequiresWebHost { get; init; }

    /// <summary>Gets the intrinsic or selected-feature reason the module requires a Web host.</summary>
    public string? WebHostRequirementReason { get; init; }

    /// <summary>Gets the intentional disable reason, when present.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>Gets the longest dependency path ending at this module.</summary>
    public int DependencyDepth { get; init; }

    /// <summary>Gets the number of direct required modules.</summary>
    public int DirectDependencyCount { get; init; }

    /// <summary>Gets the number of modules that directly require this module.</summary>
    public int DirectDependentCount { get; init; }

    /// <summary>Gets aggregate completed serial callback time in milliseconds.</summary>
    public double SerialCallbackDurationMs { get; init; }

    /// <summary>Gets aggregate startup-work execution time in milliseconds.</summary>
    public double StartupWorkDurationMs { get; init; }

    /// <summary>Gets the number of failures associated with this module.</summary>
    public int ErrorCount { get; init; }
}
