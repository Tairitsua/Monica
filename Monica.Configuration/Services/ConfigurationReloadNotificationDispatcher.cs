using Microsoft.Extensions.Logging;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services;

/// <summary>
/// Delivers reload signals without allowing transport failures to invalidate committed configuration data.
/// </summary>
internal sealed class ConfigurationReloadNotificationDispatcher(
    IEnumerable<IConfigurationChangeNotifier> notifiers,
    ILogger<ConfigurationReloadNotificationDispatcher> logger,
    ConfigurationMetricsRecorder metricsRecorder)
{
    public async Task<IReadOnlyList<ConfigurationPostCommitIssue>> DispatchAsync(
        ConfigurationReloadSignal signal,
        string operation,
        CancellationToken cancellationToken)
    {
        var issues = new List<ConfigurationPostCommitIssue>();
        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.NotifyAsync(signal, cancellationToken);
            }
            catch (Exception ex)
            {
                var notifierName = notifier.GetType().Name;
                logger.LogWarning(
                    ex,
                    "Configuration reload notification failed in {Notifier} during {Operation} for signal {SignalId}.",
                    notifierName,
                    operation,
                    signal.SignalId);
                metricsRecorder.RecordNotificationFailure(notifierName, operation, ex.GetType().Name);
                issues.Add(new ConfigurationPostCommitIssue
                {
                    Kind = ConfigurationPostCommitIssueKind.DistributedNotification,
                    Source = notifierName,
                    Message = "Configuration was saved, but a distributed reload notification failed.",
                    Detail = "Review server logs for the reload notification failure."
                });
            }
        }

        return issues;
    }
}
