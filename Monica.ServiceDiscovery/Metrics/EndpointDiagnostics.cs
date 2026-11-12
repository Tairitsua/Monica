using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;
using Monica.Modules;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Metrics;

/// <summary>Meter identity for host-owned exporters of resolution and HTTP-header observations.</summary>
public static class ServiceDiscoveryMetricNames
{
    /// <summary>Meter emitted by endpoint discovery.</summary>
    public const string MeterName = "Monica.ServiceDiscovery";
}

internal sealed class EndpointDiagnostics
{
    private readonly object _gate = new();
    private readonly ModuleServiceEndpointDiscoveryOption _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _entries;
    private readonly string[] _providers;
    private readonly Histogram<double> _resolutionDuration;
    private readonly Histogram<double> _httpDuration;

    public EndpointDiagnostics(IOptions<ModuleServiceEndpointDiscoveryOption> options, TimeProvider time,
        IEnumerable<IServiceEndpointProviderFactory> providers, IMeterFactory meters)
    {
        _options = options.Value;
        _time = time;
        _providers = providers.Select(p => p.GetType().Name).ToArray();
        _entries = _options.Targets.ToDictionary(t => t.Address.GetLeftPart(UriPartial.Authority),
            t => new Entry(t), StringComparer.OrdinalIgnoreCase);
        var meter = meters.Create(ServiceDiscoveryMetricNames.MeterName);
        _resolutionDuration = meter.CreateHistogram<double>("monica.discovery.resolve.duration", "s");
        _httpDuration = meter.CreateHistogram<double>("monica.discovery.http.duration", "s");
    }

    public ServiceEndpointTarget? Find(Uri? address)
    {
        if (address is null) return null;
        return _entries.GetValueOrDefault(address.GetLeftPart(UriPartial.Authority))?.Target;
    }

    public void Resolution(ServiceEndpointTarget target, string outcome, DiscoveredEndpoint[]? endpoints, double seconds)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var entry = _entries[target.Address.GetLeftPart(UriPartial.Authority)];
            entry.AttemptedAt = now;
            entry.Resolution = outcome;
            if (endpoints is not null)
            {
                entry.ResolvedAt = now;
                entry.Candidates = endpoints;
            }
        }
        _resolutionDuration.Record(seconds, new("service.name", target.Address.Host), new("outcome", outcome));
    }

    public void Http(ServiceEndpointTarget target, EndpointHttpObservation observation)
    {
        lock (_gate)
        {
            var entry = _entries[target.Address.GetLeftPart(UriPartial.Authority)];
            entry.Calls.Enqueue(observation);
            Prune(entry, _time.GetUtcNow());
        }
        _httpDuration.Record(observation.DurationMs / 1000, new("service.name", target.Address.Host),
            new("scope", target.Scope), new("outcome", observation.Outcome));
    }

    public ServiceEndpointDiagnosticsSnapshot Capture()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach (var entry in _entries.Values) Prune(entry, now);
            return new(Environment.MachineName, Environment.ProcessId, now, _options.Retention,
                _options.MaxCallsPerTarget, _providers.ToArray(), _entries.Values.Select(e =>
                    new ServiceEndpointSnapshot(e.Target, e.AttemptedAt, e.ResolvedAt, e.Resolution,
                        e.Candidates.ToArray(), e.Calls.ToArray())).ToArray());
        }
    }

    public static string Address(EndPoint endpoint) => endpoint switch
    {
        UriEndPoint uri => Authority(uri.Uri),
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        IPEndPoint ip => ip.ToString(),
        _ => endpoint.GetType().Name
    };

    internal static string Authority(Uri uri) => new UriBuilder(uri)
    {
        UserName = "", Password = "", Path = "", Query = "", Fragment = ""
    }.Uri.GetLeftPart(UriPartial.Authority);

    private void Prune(Entry entry, DateTimeOffset now)
    {
        while (entry.Calls.Count > 0 && (entry.Calls.Count > _options.MaxCallsPerTarget ||
               now - entry.Calls.Peek().At > _options.Retention)) entry.Calls.Dequeue();
    }

    private sealed class Entry(ServiceEndpointTarget target)
    {
        public ServiceEndpointTarget Target { get; } = target;
        public DateTimeOffset? AttemptedAt { get; set; }
        public DateTimeOffset? ResolvedAt { get; set; }
        public string Resolution { get; set; } = "Unknown";
        public DiscoveredEndpoint[] Candidates { get; set; } = [];
        public Queue<EndpointHttpObservation> Calls { get; } = new();
    }
}
