namespace Monica.JobScheduler.Models;

/// <summary>Identifies one fenced scheduling or maintenance coordinator grant.</summary>
public sealed record JobCoordinatorLeaseKey
{
    /// <summary>Gets the durable scheduler namespace.</summary>
    public required string SchedulerScopeKey { get; init; }
    /// <summary>Gets the coordination partition: an owner-prefixed key or the scope maintenance key.</summary>
    public required string CoordinatorKey { get; init; }
    /// <summary>Gets the concrete process holding this grant.</summary>
    public required string WorkerInstanceId { get; init; }
    /// <summary>Gets the opaque token that fences previous coordinator grants.</summary>
    public required string LeaseToken { get; init; }

    internal void ValidatePartition(string scope, string partition)
    {
        if (SchedulerScopeKey != scope || CoordinatorKey != partition)
            throw new InvalidOperationException("The coordinator grant belongs to a different scheduling partition.");
    }
}

/// <summary>Contains an authoritative coordinator grant and its database-time validity interval.</summary>
public sealed record JobCoordinatorLease
{
    /// <summary>Gets the key required by subsequent coordinated mutations.</summary>
    public required JobCoordinatorLeaseKey Key { get; init; }
    /// <summary>Gets the authoritative time at grant or renewal.</summary>
    public required DateTimeOffset GrantedAtUtc { get; init; }
    /// <summary>Gets the authoritative expiration of the grant.</summary>
    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
