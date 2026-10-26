namespace Monica.JobScheduler.Models.Operations;

/// <summary>Captures durable coordinator ownership in one scope without exposing fencing tokens.</summary>
/// <remarks>This observation is diagnostic evidence, never permission to perform a coordinated mutation.</remarks>
public sealed record JobCoordinatorSnapshot
{
    /// <summary>Gets the store-authoritative UTC time sampled for this observation.</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Gets retained coordinator records, including expired grants, ordered by coordinator key.</summary>
    public required IReadOnlyList<JobCoordinatorSummary> Coordinators { get; init; }
}

/// <summary>Identifies the recorded holder and expiry of one scheduling or maintenance partition.</summary>
public sealed record JobCoordinatorSummary
{
    /// <summary>Gets the partition key, such as <c>owner:Flight</c> or <c>maintenance</c>.</summary>
    public required string CoordinatorKey { get; init; }

    /// <summary>Gets the worker instance that most recently held the grant.</summary>
    public required string WorkerInstanceId { get; init; }

    /// <summary>Gets the grant's expiry according to the authoritative store clock.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
