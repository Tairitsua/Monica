using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Models;
using Monica.Modules;

namespace Monica.Configuration.Services;

/// <summary>
/// Receives remote configuration reload signals and applies locally relevant Monica projection reloads.
/// </summary>
internal sealed class ConfigurationReloadSignalReceiver(
    IConfigurationDefinitionRegistry definitionRegistry,
    ConfigurationDefinitionResolver definitionResolver,
    IConfigurationReloadCoordinator reloadCoordinator,
    IOptions<ModuleConfigurationOption> options)
    : IConfigurationReloadSignalReceiver
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNotificationIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long?> _pendingVersionsByDefinition = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _scheduleLock = new();
    private bool _reloadAllPending;
    private Task? _scheduledFlush;

    /// <inheritdoc />
    public Task ReceiveAsync(ConfigurationReloadSignal signal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShouldReceive(signal))
        {
            return Task.CompletedTask;
        }

        // Reload notifications are the closest distributed metadata freshness boundary available today. Mutation
        // workflows still resolve schemas authoritatively, so a missed notification can only stale management reads.
        definitionResolver.InvalidateReadSnapshot();

        if (signal.Kind == ConfigurationReloadSignalKind.ReloadAll)
        {
            lock (_scheduleLock)
            {
                _reloadAllPending = true;
                _pendingVersionsByDefinition.Clear();
                ScheduleFlushLocked();
            }
            return Task.CompletedTask;
        }

        foreach (var definition in signal.Definitions)
        {
            if (!ShouldReloadDefinition(definition))
            {
                continue;
            }

            _pendingVersionsByDefinition.AddOrUpdate(
                definition.DefinitionKey,
                definition.Version,
                (_, existing) => NewerVersion(existing, definition.Version));
        }

        ScheduleFlush();
        return Task.CompletedTask;
    }

    private bool ShouldReceive(ConfigurationReloadSignal signal)
    {
        var currentOptions = options.Value;
        PruneSeenNotifications(currentOptions);

        if (string.IsNullOrWhiteSpace(signal.SignalId)
            || !_seenNotificationIds.TryAdd(signal.SignalId, DateTimeOffset.UtcNow))
        {
            return false;
        }

        if (string.Equals(signal.OriginInstanceId, currentOptions.InstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        return signal.Kind is ConfigurationReloadSignalKind.DefinitionsChanged or ConfigurationReloadSignalKind.ReloadAll;
    }

    private bool ShouldReloadDefinition(ConfigurationReloadDefinitionVersion definition)
    {
        if (!definitionRegistry.TryGet(definition.DefinitionKey, out var local)
            || local?.Kind == ConfigurationKind.Startup)
        {
            return false;
        }

        if (definition.Version is { } version
            && reloadCoordinator.GetLoadedMonicaProjectionVersion(definition.DefinitionKey) is { } loadedVersion
            && loadedVersion >= version)
        {
            return false;
        }

        return true;
    }

    private void ScheduleFlush()
    {
        lock (_scheduleLock)
        {
            ScheduleFlushLocked();
        }
    }

    private void ScheduleFlushLocked()
    {
        if (_scheduledFlush is { IsCompleted: false })
        {
            return;
        }

        _scheduledFlush = FlushAfterDelayAsync();
    }

    private async Task FlushAfterDelayAsync()
    {
        var currentOptions = options.Value;
        var debounceDelay = NonNegative(currentOptions.RemoteReloadDebounceDelay);
        if (debounceDelay > TimeSpan.Zero)
        {
            await Task.Delay(debounceDelay);
        }

        var jitterDelay = RandomJitter(NonNegative(currentOptions.RemoteReloadMaxJitterDelay));
        if (jitterDelay > TimeSpan.Zero)
        {
            await Task.Delay(jitterDelay);
        }

        var (reloadAll, pending) = DrainPendingReloads();
        try
        {
            if (reloadAll)
            {
                await reloadCoordinator.ReloadMonicaProjectionAsync(CancellationToken.None);
            }
            else
            {
                foreach (var (definitionKey, version) in pending.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    await reloadCoordinator.ReloadMonicaProjectionAsync(definitionKey, version, CancellationToken.None);
                }
            }
        }
        finally
        {
            lock (_scheduleLock)
            {
                _scheduledFlush = null;
                if (_reloadAllPending || !_pendingVersionsByDefinition.IsEmpty)
                {
                    ScheduleFlushLocked();
                }
            }
        }
    }

    private (bool ReloadAll, Dictionary<string, long?> Pending) DrainPendingReloads()
    {
        var pending = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        bool reloadAll;
        lock (_scheduleLock)
        {
            reloadAll = _reloadAllPending;
            _reloadAllPending = false;
        }

        foreach (var (definitionKey, version) in _pendingVersionsByDefinition)
        {
            if (_pendingVersionsByDefinition.TryRemove(definitionKey, out var removedVersion))
            {
                pending[definitionKey] = removedVersion ?? version;
            }
        }

        return (reloadAll, pending);
    }

    private void PruneSeenNotifications(ModuleConfigurationOption currentOptions)
    {
        var window = NonNegative(currentOptions.RemoteReloadDedupeWindow);
        if (window == TimeSpan.Zero)
        {
            _seenNotificationIds.Clear();
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - window;
        foreach (var (notificationId, seenAt) in _seenNotificationIds)
        {
            if (seenAt < cutoff)
            {
                _seenNotificationIds.TryRemove(notificationId, out _);
            }
        }
    }

    private static long? NewerVersion(long? left, long? right)
    {
        return left is null || right is null ? left ?? right : Math.Max(left.Value, right.Value);
    }

    private static TimeSpan RandomJitter(TimeSpan maxDelay)
    {
        if (maxDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var maxMilliseconds = (int)Math.Min(maxDelay.TotalMilliseconds, int.MaxValue - 1);
        return TimeSpan.FromMilliseconds(Random.Shared.Next(maxMilliseconds + 1));
    }

    private static TimeSpan NonNegative(TimeSpan value)
    {
        return value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }
}
