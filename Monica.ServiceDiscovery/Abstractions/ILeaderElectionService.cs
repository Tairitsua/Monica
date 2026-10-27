using Monica.Locker.Leases;
using Monica.ServiceDiscovery.Events;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Abstractions;

/// <summary>Observes locally confirmed leadership. A new acquisition always has a new lifetime.</summary>
public interface ILeaderElectionService
{
    /// <summary>Current local role, with expired terms reported as followers.</summary>
    LeaderStatus CurrentStatus { get; }
    /// <summary>Whether a locally confirmed, uncancelled term is available.</summary>
    bool IsLeader { get; }
    /// <summary>Local acquisition time; renewal does not reset it.</summary>
    DateTime? LeaderBecomeTime { get; }
    /// <summary>Current opaque acquisition identifier, not an increasing fencing sequence.</summary>
    string? CurrentTermId { get; }
    /// <summary>Current immutable term. Pass its stopping token to all leader-scoped work.</summary>
    LeaseContext? CurrentLease { get; }
    /// <summary>Ordered asynchronous notification of a new term; check its token before starting work.</summary>
    event EventHandler<LeaderGainedEvent>? OnLeaderGained;
    /// <summary>Ordered notification that the identified term ended. Cancellation does not wait for this notification.</summary>
    event EventHandler<LeaderLostEvent>? OnLeaderLost;
    /// <summary>Stops renewal and signals local loss. The grant expires without early release of subscriber-owned work.</summary>
    void TriggerLeaderLost(LeaderLostReason reason);
}
