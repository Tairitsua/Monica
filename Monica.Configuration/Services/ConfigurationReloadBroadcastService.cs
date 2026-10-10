using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Models;
using Monica.Modules;

namespace Monica.Configuration.Services;

internal sealed class ConfigurationReloadBroadcastService(
    IConfigurationReloadCoordinator reloadCoordinator,
    ConfigurationReloadNotificationDispatcher notificationDispatcher,
    IConfigurationEffectiveValueStore effectiveValueStore,
    IOptions<ModuleConfigurationOption> moduleOptions,
    ILogger<ConfigurationReloadBroadcastService> logger)
    : IConfigurationReloadBroadcastService
{
    public async Task<ConfigurationReloadBroadcastResult> BroadcastAllAsync(CancellationToken cancellationToken)
    {
        var issues = new List<ConfigurationPostCommitIssue>();
        var localReloadSucceeded = true;
        try
        {
            await reloadCoordinator.ReloadMonicaProjectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local Monica projection reload failed during a reload-all broadcast.");
            localReloadSucceeded = false;
            issues.Add(new ConfigurationPostCommitIssue
            {
                Kind = ConfigurationPostCommitIssueKind.LocalReload,
                Source = nameof(ConfigurationReloadBroadcastService),
                Message = "The current process could not reload its Monica configuration projection.",
                Detail = "Review server logs for the local projection reload failure."
            });
        }

        var signal = new ConfigurationReloadSignal
        {
            SignalId = Guid.NewGuid().ToString("N"),
            OriginInstanceId = moduleOptions.Value.InstanceId,
            StoreKey = effectiveValueStore.Descriptor.StoreKey,
            Kind = ConfigurationReloadSignalKind.ReloadAll
        };
        issues.AddRange(await notificationDispatcher.DispatchAsync(signal, "reload_all", cancellationToken));

        return new ConfigurationReloadBroadcastResult
        {
            LocalReloadSucceeded = localReloadSucceeded,
            PostCommitIssues = issues
        };
    }
}
