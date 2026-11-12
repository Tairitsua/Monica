namespace Monica.ServiceDiscovery.Models;

/// <summary>
/// Identifies a host-declared logical destination. Address is an HTTP(S) discovery URI, optionally
/// using Microsoft's named-endpoint syntax. It contains no credentials, path, query or fragment.
/// Scope and AppId are diagnostic identity; they never override application routing.
/// </summary>
public sealed record ServiceEndpointTarget(string Scope, string AppId, Uri Address);

/// <summary>A detached candidate address and the provider reported by Microsoft's endpoint feature.</summary>
public sealed record DiscoveredEndpoint(string Address, string Provider);

/// <summary>
/// A process-local HTTP observation, ending at receipt of response headers. Success here means
/// HTTP 2xx only, not successful response decoding or business execution. Endpoint excludes paths.
/// </summary>
public sealed record EndpointHttpObservation(
    DateTimeOffset At, string? Endpoint, int? StatusCode, string Outcome, double DurationMs, string? TraceId);

/// <summary>
/// Detached diagnostic evidence. Candidates retain the last successful capture after a failed refresh.
/// Calls are limited by both retention time and count. No calls means unknown, not zero error rate.
/// </summary>
public sealed record ServiceEndpointSnapshot(
    ServiceEndpointTarget Target, DateTimeOffset? AttemptedAt, DateTimeOffset? ResolvedAt,
    string Resolution, IReadOnlyList<DiscoveredEndpoint> Candidates,
    IReadOnlyList<EndpointHttpObservation> Calls);

/// <summary>Host-owned evidence with explicit process identity, capture time and retention limits.</summary>
public sealed record ServiceEndpointDiagnosticsSnapshot(
    string Host, int ProcessId, DateTimeOffset CapturedAt, TimeSpan Retention, int MaxCallsPerTarget,
    IReadOnlyList<string> Providers, IReadOnlyList<ServiceEndpointSnapshot> Targets);
