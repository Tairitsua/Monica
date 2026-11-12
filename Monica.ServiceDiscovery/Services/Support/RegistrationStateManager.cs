using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;
using Monica.StateStore.Abstractions;
using Monica.Core;
using Monica.Locker.Leases;

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
    [FromKeyedServices(nameof(ModuleServiceDiscovery))] IExclusiveLeaseStore leases,
    MonicaApplication? application = null)
    : IRegistrationStateManager
{
    private readonly ModuleServiceDiscoveryOption _option = options.Value;

    private const string REG_PREFIX = "reg:";

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

    public async Task<bool> LeaderExistsAsync(CancellationToken ct = default) =>
        await GetLeaderStateAsync(ct) is not null;

    public async Task<LeaderState?> GetLeaderStateAsync(CancellationToken ct = default)
    {
        var appId = clientInfo.GetServiceStatus().AppId;
        var resource = LeaderElectionService.Resource(_option, appId);
        var snapshot = await leases.GetSnapshotAsync(resource, ct);
        var leader = snapshot.Leases.SingleOrDefault(row => row.Resource == resource
            && row.ExpiresAtUtc > snapshot.ObservedAtUtc);
        return leader is null ? null : new LeaderState
        {
            AppId = appId, InstanceId = leader.HolderId, BecomeLeaderTime = leader.AcquiredAtUtc.UtcDateTime
        };
    }

    public async Task ForceDeleteLeaderKeyAsync(string appId, CancellationToken ct = default)
    {
        EnsureParticipation();
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (leases is not ILeaseAdministration administration)
            throw new NotSupportedException("The selected lease authority does not support administrative revocation.");
        await administration.RevokeAsync(LeaderElectionService.Resource(_option, appId), ct);
        if (appId == clientInfo.GetServiceStatus().AppId)
            leaderElectionService.TriggerLeaderLost(Monica.ServiceDiscovery.Events.LeaderLostReason.LeaderKeyExpired);
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
        var scope = _option.LeaderLeaseStore?.Scope ?? "service-discovery";
        var snapshot = await leases.GetSnapshotAsync(scope, LeaderElectionService.RESOURCE_PREFIX, ct);
        var keys = snapshot.Leases.Where(row => row.ExpiresAtUtc > snapshot.ObservedAtUtc)
            .Select(row => REG_PREFIX + row.Resource.Name[LeaderElectionService.RESOURCE_PREFIX.Length..]
                + ":" + row.HolderId).ToList();
        if (keys.Count == 0) return [];
        var instances = await stateStore.GetBulkStateAsync<InstanceState>(keys, removeEmptyValue: true, cancellationToken: ct);
        return instances.Values.Where(instance => instance is not null).ToList()!;
    }
}
