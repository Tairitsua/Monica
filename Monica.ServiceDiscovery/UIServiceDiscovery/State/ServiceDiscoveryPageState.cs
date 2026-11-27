using Monica.Core.Results;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.UIServiceDiscovery.Support;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.State;

/// <summary>Retains the latest complete membership capture and polling preference for this circuit.</summary>
public sealed class ServiceDiscoveryPageState(ServiceDiscoveryFacade facade, ServiceInstanceEvictionTracker evictionTracker)
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);

    internal DiscoveryEvidence? Evidence { get; private set; }
    internal string? Error { get; private set; }
    internal bool AutoRefresh { get; set; } = true;
    internal static TimeSpan RefreshInterval { get; } = TimeSpan.FromSeconds(3);

    internal async Task RefreshAsync(CancellationToken token)
    {
        await _captureGate.WaitAsync(token);
        try
        {
            var services = await facade.CaptureServicesAsync(token);
            if (services.IsFailure()) { Error = services.Message; return; }
            var topology = await facade.CaptureTopologyAsync(token);
            if (topology.IsFailure()) { Error = topology.Message; return; }
            var domains = await facade.CaptureDomainsAsync(token);
            if (domains.IsFailure()) { Error = domains.Message; return; }
            var host = await facade.CaptureCurrentInstanceAsync(token);
            if (host.IsFailure()) { Error = host.Message; return; }
            var policy = facade.GetElectionConfig().Data!;
            token.ThrowIfCancellationRequested();

            Evidence = new DiscoveryEvidence(DateTime.UtcNow, policy, evictionTracker.Apply(services.Data!),
                domains.Data!, topology.Data!, host.Data!);
            Error = null;
        }
        finally { _captureGate.Release(); }
    }
}
