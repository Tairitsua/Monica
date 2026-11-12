using Monica.Locker.Leases;
using Monica.JobScheduler.Services.Support;

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

    internal LeaseIdentity ToIdentity() => new(new LeaseResource(SchedulerScopeKey,
        JobCoordinatorLeaseAdapter.PREFIX + CoordinatorKey), WorkerInstanceId, LeaseToken);

    internal void ValidatePartition(string scope, string partition)
    {
        if (SchedulerScopeKey != scope || CoordinatorKey != partition)
            throw new InvalidOperationException("The coordinator grant belongs to a different scheduling partition.");
    }
}
