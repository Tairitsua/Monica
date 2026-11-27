namespace Monica.SignalR.Models;

/// <summary>
/// Snapshot of Monica-observed SignalR server-to-client send activity.
/// </summary>
public sealed class SignalRSendDiagnosticsSnapshot
{
    /// <summary>
    /// Gets or sets a value indicating whether send diagnostics are enabled for the module.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>Gets whether this capture includes explicit target identifiers; false means count-only privacy rendering.</summary>
    /// <remarks>This reports the host's identifier-capture policy even when all retained rows target all clients.</remarks>
    public bool IncludesTargetIdentifiers { get; init; }

    /// <summary>
    /// Gets or sets the UTC time when this snapshot was produced.
    /// </summary>
    public DateTime SnapshotTimeUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets a note that explains what the metric can and cannot represent.
    /// </summary>
    public string Description { get; init; } =
        "These counters measure Monica-observed pending SignalR send tasks, not ASP.NET Core SignalR private transport queue depth.";

    /// <summary>
    /// Gets or sets the total number of currently in-flight observed send tasks.
    /// </summary>
    public long PendingSendCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks started since process start.
    /// </summary>
    public long TotalStartedCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks completed successfully since process start.
    /// </summary>
    public long TotalCompletedCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks that failed since process start.
    /// </summary>
    public long TotalFailedCount { get; init; }

    /// <summary>
    /// Gets or sets the method-level send metrics used by the default debug UI view.
    /// </summary>
    /// <remarks>
    /// These rows are aggregated by hub name and client contract method name. Use
    /// <see cref="SignalRSendMethodMetricInfo.TargetMetrics"/> or <see cref="Metrics"/> when target-level
    /// details are needed.
    /// </remarks>
    public List<SignalRSendMethodMetricInfo> MethodMetrics { get; init; } = [];

    /// <summary>
    /// Gets or sets the observed target-level metrics retained for drill-down diagnostics.
    /// </summary>
    /// <remarks>
    /// Target-level rows are useful for diagnosing hot users, groups, or connection selections, but they do not
    /// represent SignalR private transport queue depth.
    /// </remarks>
    public List<SignalRSendMetricInfo> Metrics { get; init; } = [];
}

/// <summary>
/// Aggregated send diagnostics for a hub method across all observed target selections.
/// </summary>
public sealed class SignalRSendMethodMetricInfo
{
    /// <summary>
    /// Gets or sets the hub type name that emitted the sends.
    /// </summary>
    public required string HubName { get; init; }

    /// <summary>
    /// Gets or sets the strongly typed client contract method name.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Gets or sets the number of target-level metric rows represented by this method summary.
    /// </summary>
    /// <remarks>
    /// This is a diagnostics row count, not a recipient count, online connection count, or SignalR internal queue
    /// depth.
    /// </remarks>
    public int TargetMetricCount { get; init; }

    /// <summary>
    /// Gets or sets the current number of in-flight send tasks for this method.
    /// </summary>
    public long PendingSendCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks started for this method.
    /// </summary>
    public long StartedCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks completed successfully for this method.
    /// </summary>
    public long CompletedCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks that failed for this method.
    /// </summary>
    public long FailedCount { get; init; }

    /// <summary>
    /// Gets or sets the last observed send duration in milliseconds from the most recent target-level metric.
    /// </summary>
    public double LastDurationMilliseconds { get; init; }

    /// <summary>
    /// Gets or sets the maximum observed send duration in milliseconds across all target-level metrics.
    /// </summary>
    public double MaxDurationMilliseconds { get; init; }

    /// <summary>
    /// Gets or sets the weighted average observed send duration in milliseconds across completed and failed sends.
    /// </summary>
    public double AverageDurationMilliseconds { get; init; }

    /// <summary>
    /// Gets or sets the UTC time when a send was last started for this method.
    /// </summary>
    public DateTime? LastStartedAtUtc { get; init; }

    /// <summary>
    /// Gets or sets the UTC time when a send last completed or failed for this method.
    /// </summary>
    public DateTime? LastCompletedAtUtc { get; init; }

    /// <summary>
    /// Gets or sets the target-level metrics that make up this method summary.
    /// </summary>
    /// <remarks>
    /// These rows preserve the underlying target selections so a method-level backlog can still be traced to
    /// specific users, groups, or connection selections when target identifier capture is enabled.
    /// </remarks>
    public List<SignalRSendMetricInfo> TargetMetrics { get; init; } = [];
}

/// <summary>
/// Aggregated send diagnostics for a hub method and target selection.
/// </summary>
public sealed class SignalRSendMetricInfo
{
    /// <summary>
    /// Gets or sets the hub type name that emitted the send.
    /// </summary>
    public required string HubName { get; init; }

    /// <summary>
    /// Gets or sets the strongly typed client contract method name.
    /// </summary>
    public required string MethodName { get; init; }

    /// <summary>
    /// Gets or sets the kind of client target used for the send.
    /// </summary>
    public SignalRSendTargetKind TargetKind { get; init; }

    /// <summary>
    /// Gets or sets the number of explicit target or exclusion identifiers supplied for this target selection.
    /// </summary>
    /// <remarks>
    /// This value is not an online recipient count, connected client count, or SignalR internal queue depth.
    /// <see cref="SignalRSendTargetKind.All"/> has no explicit target identifiers, so the value is zero.
    /// </remarks>
    public int TargetCount { get; init; }

    /// <summary>
    /// Gets or sets the explicit target or exclusion identifiers when target identifier capture is enabled.
    /// </summary>
    public List<string> TargetIdentifiers { get; init; } = [];

    /// <summary>
    /// Gets or sets the human-friendly display names aligned by index with <see cref="TargetIdentifiers"/>.
    /// </summary>
    /// <remarks>
    /// Names are resolved from the online connection registry at capture time, so entries are empty for
    /// identifiers without a resolvable display name (for example a user target with no live connection).
    /// Renderers fall back to the raw identifier for empty entries.
    /// </remarks>
    public List<string> TargetIdentifierDisplayNames { get; init; } = [];

    /// <summary>
    /// Gets or sets the current number of in-flight send tasks for this metric.
    /// </summary>
    public long PendingSendCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks started for this metric.
    /// </summary>
    public long StartedCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks completed successfully for this metric.
    /// </summary>
    public long CompletedCount { get; init; }

    /// <summary>
    /// Gets or sets the total number of observed send tasks that failed for this metric.
    /// </summary>
    public long FailedCount { get; init; }

    /// <summary>
    /// Gets or sets the last observed send duration in milliseconds.
    /// </summary>
    public double LastDurationMilliseconds { get; init; }

    /// <summary>
    /// Gets or sets the maximum observed send duration in milliseconds.
    /// </summary>
    public double MaxDurationMilliseconds { get; init; }

    /// <summary>
    /// Gets or sets the average observed send duration in milliseconds.
    /// </summary>
    public double AverageDurationMilliseconds { get; init; }

    /// <summary>
    /// Gets or sets the UTC time when a send was last started for this metric.
    /// </summary>
    public DateTime? LastStartedAtUtc { get; init; }

    /// <summary>
    /// Gets or sets the UTC time when a send last completed or failed for this metric.
    /// </summary>
    public DateTime? LastCompletedAtUtc { get; init; }
}
