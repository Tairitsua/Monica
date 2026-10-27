namespace Monica.ServiceDiscovery.Models;

public sealed class CurrentInstanceSnapshot
{
    public required InstanceState CurrentInstance { get; init; }

    public bool IsLeaderElectionEnabled { get; init; }

    public LeaderStatus CurrentLeaderStatus { get; init; }

    public bool IsLeader { get; init; }

    public DateTime? LeaderBecomeTime { get; init; }

    /// <summary>Opaque local leadership acquisition ID, or null when not leader; this is not an ordered fence.</summary>
    public string? CurrentTermId { get; init; }

    public LeaderState? ClusterLeaderState { get; init; }
}
