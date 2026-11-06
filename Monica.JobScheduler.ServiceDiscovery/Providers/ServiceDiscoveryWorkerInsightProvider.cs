using Monica.Core.Results;
using Monica.JobScheduler.UI.UIJobScheduler.Abstractions;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Models;

namespace Monica.JobScheduler.ServiceDiscovery.Providers;

/// <summary>
/// Projects the ServiceDiscovery registry's heartbeat view onto the JobScheduler worker-instance insight model.
/// Liveness classification mirrors the ServiceDiscovery UI's client-side thresholds: fresher than 1.5 heartbeat
/// periods is online, below the registration TTL is unhealthy, and anything older is offline.
/// </summary>
internal sealed class ServiceDiscoveryWorkerInsightProvider(
    ServiceDiscoveryFacade facade,
    TimeProvider timeProvider) : IJobSchedulerWorkerInsightProvider
{
    private const double HEALTHY_HEARTBEAT_PERIODS = 1.5;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSchedulerWorkerInstanceView>> GetWorkerInstancesAsync(
        CancellationToken cancellationToken = default)
    {
        var servicesResult = await facade.GetServicesStatusAsync();
        if (servicesResult.IsFailure())
        {
            throw new InvalidOperationException(servicesResult.Message);
        }

        var services = servicesResult.Data
            ?? throw new InvalidOperationException("Service discovery returned no service status data.");

        cancellationToken.ThrowIfCancellationRequested();
        // The election configuration only carries heartbeat cadence constants; an unavailable config falls back to
        // the module defaults instead of hiding every instance.
        var electionResult = facade.GetElectionConfig();
        var electionConfig = electionResult.IsFailure() ? null : electionResult.Data;
        var healthyBoundary = (electionConfig?.HeartbeatPeriod ?? TimeSpan.FromSeconds(15))
            * HEALTHY_HEARTBEAT_PERIODS;
        var registrationTtl = electionConfig?.RegistrationTTL ?? TimeSpan.FromSeconds(46);
        var now = timeProvider.GetUtcNow();

        return services
            .SelectMany(static service => service.Instances.Values)
            .Select(instance => new JobSchedulerWorkerInstanceView
            {
                AppId = instance.AppId,
                AppName = instance.AppName,
                ProjectName = instance.ProjectName,
                InstanceId = instance.InstanceId,
                IsLeader = instance.IsLeader,
                RegisteredAtUtc = ToUtcOffset(instance.RegistrationTime),
                LastHeartbeatUtc = ToUtcOffset(instance.LastHeartbeatTime),
                Status = Classify(now - ToUtcOffset(instance.LastHeartbeatTime), healthyBoundary, registrationTtl),
                ListeningAddresses = SplitListeningAddresses(instance.Metadata)
            })
            .OrderBy(static view => view.ProjectName, StringComparer.Ordinal)
            .ThenBy(static view => view.InstanceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static JobSchedulerWorkerInstanceStatus Classify(
        TimeSpan heartbeatAge,
        TimeSpan healthyBoundary,
        TimeSpan registrationTtl) =>
        heartbeatAge <= healthyBoundary
            ? JobSchedulerWorkerInstanceStatus.Online
            : heartbeatAge < registrationTtl
                ? JobSchedulerWorkerInstanceStatus.Unhealthy
                : JobSchedulerWorkerInstanceStatus.Offline;

    // ServiceDiscovery persists its timestamps as UTC DateTimes; unspecified kind values are treated as UTC so
    // they remain comparable with the snapshot's UTC capture instant.
    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static IReadOnlyList<string> SplitListeningAddresses(Dictionary<string, string>? metadata) =>
        metadata is not null
        && metadata.TryGetValue(IServiceDiscoveryClientInfo.LISTENING_ADDRESS_METADATA_KEY, out var joined)
        && !string.IsNullOrWhiteSpace(joined)
            ? joined.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
}
