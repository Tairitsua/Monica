using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Abstractions;

/// <summary>
/// Registration results
/// </summary>
public record RegistrationResult(bool Success, DateTime? HeartbeatTime, string? ErrorMessage);

/// <summary>
/// Registration status management interface
/// </summary>
public interface IRegistrationStateManager
{
    /// <summary>
    /// Registration or heartbeat (the first registration is also a heartbeat, because inst_id will not be repeated)
    /// </summary>
    Task<RegistrationResult> RegisterOrHeartbeatAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if Leader Key exists
    /// </summary>
    Task<bool> LeaderExistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the querying application's leader lease. A successful absent key returns null.
    /// Provider failures and cancellation propagate; they must not be represented as an absent lease.
    /// </summary>
    Task<LeaderState?> GetLeaderStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads all unexpired registration records in the selected store. An empty result means a
    /// successful read found no registrations. Provider enumeration/read failures and cancellation propagate.
    /// </summary>
    Task<List<InstanceState>> GetAllInstancesAsync(CancellationToken ct = default);
    /// <summary>
    /// Get all Leader instance status
    /// </summary>
    Task<List<InstanceState>> GetAllLeaderInstancesAsync(CancellationToken ct = default);

    /// <summary>
    /// Forcefully delete the Leader Key of the specified application (for management/debugging).
    /// </summary>
    Task ForceDeleteLeaderKeyAsync(string appId, CancellationToken ct = default);
}
