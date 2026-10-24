using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.StateStore.Abstractions;
using Monica.Core;

namespace Monica.ServiceDiscovery.Services.Support;

/// <summary>
/// Registration state manager based on StateStore
/// </summary>
public class RegistrationStateManager(
    [FromKeyedServices(nameof(ModuleServiceDiscovery))] IStateStore stateStore,
    IServiceDiscoveryClientInfo clientInfo,
    ILeaderElectionService leaderElectionService,
    IOptions<ModuleServiceDiscoveryOption> options,
    ILogger<RegistrationStateManager> logger,
    MonicaApplication? application = null)
    : IRegistrationStateManager
{
    private readonly ModuleServiceDiscoveryOption _option = options.Value;

    private const string REG_PREFIX = "reg:";
    private const string LEADER_PREFIX = "leader:";

    // Cached registration time to avoid unnecessary Get operations on every heartbeat
    private DateTime? _cachedRegistrationTime;
    private readonly object _registrationTimeLock = new();

    private void EnsureParticipation()
    {
        if (application?.Application.AutonomousWork == false)
            throw new InvalidOperationException("Service-discovery mutation is unavailable while Application:AutonomousWork is false.");
    }

    /// <summary>
    /// Get registration key
    /// </summary>
    private string GetRegistrationKey() =>
        $"{(clientInfo.GetServiceStatus() is { } status ? $"{status.AppId}:{status.InstanceId}" : throw new InvalidOperationException("InstanceId is required"))}";

    /// <summary>
    /// Get Leader Key
    /// </summary>
    private string GetLeaderKey() => clientInfo.GetServiceStatus().AppId;

    public async Task<RegistrationResult> RegisterOrHeartbeatAsync(CancellationToken ct = default)
    {
        EnsureParticipation();
        try
        {
            var regKey = GetRegistrationKey();
            var now = DateTime.UtcNow;

            // Every heartbeat replaces the complete registration, including its metadata.
            var instanceState = clientInfo.GetServiceStatus(isHeartbeatInfo: false);

            // Use cached registration time to avoid Get operations for every heartbeat
            DateTime registrationTime;
            lock (_registrationTimeLock)
            {
                registrationTime = _cachedRegistrationTime ?? now;
            }

            // Set runtime status field
            instanceState.RegistrationTime = registrationTime;
            instanceState.LastHeartbeatTime = now;
            instanceState.IsLeader = leaderElectionService.IsLeader;

            await stateStore.SaveStateAsync(
                REG_PREFIX + regKey,
                instanceState,
                ct,
                _option.Election.RegistrationTTL);

            // Cache registration time after first successful save
            lock (_registrationTimeLock)
            {
                _cachedRegistrationTime ??= registrationTime;
            }

            logger.LogDebug("Heartbeat succeeded for registration key {RegistrationKey}.", regKey);
            return new RegistrationResult(true, now, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Heartbeat failed.");
            return new RegistrationResult(false, null, ex.Message);
        }
    }

    public async Task<bool> LeaderExistsAsync(CancellationToken ct = default)
    {
        try
        {
            var leaderKey = GetLeaderKey();
            return await stateStore.ExistAsync(LEADER_PREFIX + leaderKey, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to determine whether the leader key exists.");
            return false;
        }
    }

    public async Task<LeaderState?> GetLeaderStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var leaderKey = GetLeaderKey();
            var state = await stateStore.GetStateAsync<LeaderState>(LEADER_PREFIX + leaderKey, ct);
            ct.ThrowIfCancellationRequested();
            return state;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get the leader state.");
            throw;
        }
    }

    public async Task<(bool Success, LeaderState? State, string? ETag)> TryBecomeLeaderAsync(CancellationToken ct = default)
    {
        EnsureParticipation();
        try
        {
            var leaderKey = GetLeaderKey();
            var now = DateTime.UtcNow;
            var serviceStatus = clientInfo.GetServiceStatus();

            var leaderState = new LeaderState
            {
                InstanceId = serviceStatus.InstanceId,
                BecomeLeaderTime = now,
                AppId = serviceStatus.AppId
            };

            // Try to save only if Key does not exist
            var success = await stateStore.TrySaveStateIfNotExistsAsync(
                LEADER_PREFIX + leaderKey,
                leaderState,
                ct,
                _option.Election.LeaderTTL);

            if (success)
            {
                // Get ETag
                var (_, eTag) = await stateStore.GetStateAndETagAsync<LeaderState>(LEADER_PREFIX + leaderKey, ct);
                logger.LogInformation("Instance {InstanceId} acquired leadership.", serviceStatus.InstanceId);
                return (true, leaderState, eTag);
            }

            logger.LogDebug("Leadership was not acquired because the leader key already exists.");
            return (false, null, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to acquire leadership.");
            return (false, null, null);
        }
    }

    public async Task<(bool Success, string? NewETag, LeaderState? ActualState, string? ActualETag)> RenewLeaderLeaseAsync(string expectedETag, CancellationToken ct = default)
    {
        EnsureParticipation();
        try
        {
            var leaderKey = GetLeaderKey();
            var now = DateTime.UtcNow;
            var serviceStatus = clientInfo.GetServiceStatus();

            var leaderState = new LeaderState
            {
                InstanceId = serviceStatus.InstanceId,
                BecomeLeaderTime = now,
                AppId = serviceStatus.AppId
            };

            var (success, newETag) = await stateStore.TrySaveStateWithETagAsync(
                LEADER_PREFIX + leaderKey,
                leaderState,
                expectedETag,
                ct,
                _option.Election.LeaderTTL);

            if (success)
            {
                logger.LogDebug("Leader lease renewed with ETag {ETag}.", newETag);
                return (true, newETag, null, null);
            }

            var (actualState, actualETag) = await stateStore.GetStateAndETagAsync<LeaderState>(LEADER_PREFIX + leaderKey, ct);
            if (actualState is not null)
            {
                logger.LogWarning(
                    "Leader lease renewal failed because the ETag did not match. Expected ETag: {ExpectedETag}; actual ETag: {ActualETag}.",
                    expectedETag,
                    actualETag);
            }
            else
            {
                logger.LogWarning("Leader lease renewal failed because no leader state was found.");
            }

            return (false, null, actualState, actualETag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Leader lease renewal failed unexpectedly.");
            return (false, null, null, null);
        }
    }

    public async Task DeleteLeaderKeyAsync(CancellationToken ct = default)
    {
        EnsureParticipation();
        try
        {
            var leaderKey = GetLeaderKey();
            await stateStore.DeleteStateAsync(LEADER_PREFIX + leaderKey, ct);
            logger.LogInformation("Deleted leader key {LeaderKey}.", leaderKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete the leader key.");
        }
    }

    public async Task ForceDeleteLeaderKeyAsync(string appId, CancellationToken ct = default)
    {
        EnsureParticipation();
        try
        {
            await stateStore.DeleteStateAsync(LEADER_PREFIX + appId, ct);
            logger.LogInformation("Force-deleted the leader key for application {AppId}.", appId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to force-delete the leader key for application {AppId}.", appId);
            throw;
        }
    }

    public async Task<List<InstanceState>> GetAllInstancesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Step 1: Use glob pattern to scan all keys under the reg prefix
            var instanceKeys = await stateStore.ScanKeysAsync(REG_PREFIX + "*", ct);
            ct.ThrowIfCancellationRequested();

            if (instanceKeys.Count == 0)
            {
                logger.LogDebug("No registered service instances were found.");
                return [];
            }

            // Step 2: Get all InstanceState in batches
            var instances = await stateStore.GetBulkStateAsync<InstanceState>(
                instanceKeys,
                removeEmptyValue: true,
                cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            
            return instances.Values
                .Where(x => x != null)
                .ToList()!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve registered service instances.");
            throw;
        }
    }

    public async Task<List<InstanceState>> GetAllLeaderInstancesAsync(CancellationToken ct = default)
    {
        try
        {
            // Step 1: Use glob pattern to scan all keys under the leader prefix
            var leaderKeys = await stateStore.ScanKeysAsync(LEADER_PREFIX + "*", ct);

            if (leaderKeys.Count == 0)
            {
                logger.LogDebug("No leader instances were found.");
                return [];
            }

            // Step 2: Get all LeaderState in batches
            var leaderStates = await stateStore.GetBulkStateAsync<LeaderState>(
                leaderKeys,
                removeEmptyValue: true,
                cancellationToken: ct);

            // Step 3: Build the key list of InstanceState
            var instanceKeys = leaderStates.Values
                .Where(ls => ls != null && !string.IsNullOrEmpty(ls.AppId))
                .Select(ls => REG_PREFIX + $"{ls!.AppId}:{ls.InstanceId}")
                .ToList();

            if (instanceKeys.Count == 0)
            {
                logger.LogDebug("No valid leader instance keys were found.");
                return [];
            }

            // Step 4: Get the InstanceState of all Leaders in batches
            var instances = await stateStore.GetBulkStateAsync<InstanceState>(
                instanceKeys,
                removeEmptyValue: true,
                cancellationToken: ct);

            return instances.Values.Where(x => x != null).ToList()!;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve leader instances.");
            return [];
        }
    }
}
