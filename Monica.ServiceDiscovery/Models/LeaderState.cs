namespace Monica.ServiceDiscovery.Models;

/// <summary>
/// Leader status
/// </summary>
public class LeaderState
{
    /// <summary>
    /// Leader instance ID
    /// </summary>
    public required string InstanceId { get; set; }

    /// <summary>
    /// Time to become a Leader
    /// </summary>
    public DateTime BecomeLeaderTime { get; set; }

    /// <summary>The independently observed provider lease expiry in UTC, when supplied by the authority.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Application identifier that owns the leader key.
    /// </summary>
    public string? AppId { get; set; }
}
