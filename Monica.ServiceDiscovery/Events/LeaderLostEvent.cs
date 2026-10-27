using Monica.Locker.Leases;

namespace Monica.ServiceDiscovery.Events;

/// <summary>
/// Reason for leader loss
/// </summary>
public enum LeaderLostReason
{
    /// <summary>
    /// network isolation
    /// </summary>
    NetworkIsolation,

    /// <summary>
    /// Leader Key is occupied by other instances
    /// </summary>
    LeaderKeyTakenByOther,

    /// <summary>
    /// graceful closing
    /// </summary>
    GracefulShutdown,

    /// <summary>
    /// Struggle timeout
    /// </summary>
    StruggleTimeout,

    /// <summary>
    /// Leader Key has expired or been deleted
    /// </summary>
    LeaderKeyExpired
}

/// <summary>
/// Leader loss event
/// </summary>
public class LeaderLostEvent
{
    /// <summary>The ended acquisition; delayed observers must not cancel a later term.</summary>
    public required LeaseIdentity Identity { get; init; }
    /// <summary>
    /// Application identifier that lost leadership.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Instance ID
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// The time the leader was lost
    /// </summary>
    public required DateTime LostTime { get; init; }

    /// <summary>
    /// Reason for loss
    /// </summary>
    public required LeaderLostReason Reason { get; init; }
}
