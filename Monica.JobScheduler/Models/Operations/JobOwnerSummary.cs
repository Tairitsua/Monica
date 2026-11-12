namespace Monica.JobScheduler.Models.Operations;

/// <summary>
/// Aggregates one owner's durable scheduler footprint inside one scope: definition counts by presence, active
/// queue depth, accepted manifest publication, and the latest owner snapshot observation.
/// </summary>
public sealed record JobOwnerSummary
{
    /// <summary>Gets the accepted declaration manifest hash, or null before owner metadata is published.</summary>
    public string? ManifestHash { get; init; }

    /// <summary>Gets when the current manifest content was first accepted, independent of later observations.</summary>
    /// <remarks>Null means publication metadata is unavailable; it is not inferred from legacy observation times.</remarks>
    public DateTimeOffset? ManifestPublishedAtUtc { get; init; }

    /// <summary>
    /// Gets the owner identity whose snapshot published these definitions.
    /// </summary>
    public required string OwnerKey { get; init; }

    /// <summary>
    /// Gets how many of the owner's definitions its latest snapshot currently publishes.
    /// </summary>
    public required int PresentCount { get; init; }

    /// <summary>
    /// Gets how many retained definitions the owner no longer publishes. They reject new admission but keep
    /// operator policy and execution history for audit.
    /// </summary>
    public required int AbsentCount { get; init; }

    /// <summary>
    /// Gets the owner's currently queued execution count.
    /// </summary>
    public required int QueuedCount { get; init; }

    /// <summary>
    /// Gets the owner's currently running execution count.
    /// </summary>
    public required int RunningCount { get; init; }

    /// <summary>
    /// Gets the latest owner snapshot observation, including unchanged-manifest heartbeats. Legacy owners without
    /// owner metadata use their freshest definition observation. This does not establish worker readiness.
    /// </summary>
    public required DateTimeOffset LastObservedAtUtc { get; init; }
}
