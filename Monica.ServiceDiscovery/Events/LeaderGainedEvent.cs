using Monica.Locker.Leases;

namespace Monica.ServiceDiscovery.Events;

/// <summary>
/// Leader gets events
/// </summary>
public class LeaderGainedEvent
{
    /// <summary>The unique acquisition and its permanent cancellation signal.</summary>
    public required LeaseContext Lease { get; init; }
    /// <summary>
    /// Application identifier that owns leadership.
    /// </summary>
    public required string AppId { get; init; }

    /// <summary>
    /// Instance ID
    /// </summary>
    public required string InstanceId { get; init; }

    /// <summary>
    /// Time to become a Leader
    /// </summary>
    public required DateTime BecomeLeaderTime { get; init; }
}
