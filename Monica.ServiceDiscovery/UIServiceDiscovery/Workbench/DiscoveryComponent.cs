using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.Core.ApplicationRoles;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Models;
using Monica.UI.Shared.Services;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.Workbench;

/// <summary>Shared formatting used by the five service discovery diagnostic views.</summary>
public abstract class DiscoveryComponent : ComponentBase
{
    [Inject] protected IStringLocalizer<ServiceDiscoveryResource> L { get; set; } = default!;
    [Inject] protected MoTimeFormatter UiTime { get; set; } = default!;
    [CascadingParameter] private DiscoveryContext Context { get; set; } = default!;
    private protected DiscoveryWorkbench Workbench => Context.Owner;

    private protected string T(string key, params object[] values)
    {
        var separator = key.IndexOf(':');
        if (separator < 0)
            return values.Length == 0 ? L[$"Discovery:{key}"].Value : L[$"Discovery:{key}", values].Value;
        var group = key[..separator];
        var name = key[(separator + 1)..].Replace("-", string.Empty, StringComparison.Ordinal);
        return values.Length == 0 ? L[$"Discovery:{group}:{name}"].Value : L[$"Discovery:{group}:{name}", values].Value;
    }

    private protected static string Num(double? number) => number?.ToString("0.#", CultureInfo.CurrentCulture) ?? "\u2014";
    private protected static string Pct(double value, double max) =>
        Math.Clamp(value / (max > 0 ? max : 1) * 100, 0, 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private protected static DateTime? KnownTime(DateTime value) => value == default ? null : value;
    private protected static string ShortName(string value) => value[(value.LastIndexOf('.') + 1)..];
    private protected static string StatusRole(ServiceStatus status) => status switch
    {
        ServiceStatus.Running => "success", ServiceStatus.Error or ServiceStatus.Offline => "error",
        ServiceStatus.Updating => "info", ServiceStatus.Unhealthy => "warning", _ => "neutral"
    };

    private protected static string HeartbeatRole(string health) => health switch
    {
        "Healthy" => "success", "Unhealthy" => "warning", "Offline" => "error", _ => "neutral"
    };

    private protected static string HealthRole(RoleHealthStatus? status) => status switch
    {
        RoleHealthStatus.Healthy => "success", RoleHealthStatus.Degraded => "warning",
        RoleHealthStatus.Faulted => "error", RoleHealthStatus.Initializing => "info", _ => "neutral"
    };

    private protected static string RoutingRole(string route) => route switch
    {
        "remote-runonrole" => "primary", "remote-artifact" => "third", "local-runonrole" => "info", _ => "neutral"
    };

    private protected static string EndpointRole(string status) => status switch
    {
        "Resolved" => "success", "Failed" or "Timeout" => "error", "Stale" or "Empty" => "warning", _ => "neutral"
    };

    private protected static string OutcomeRole(string outcome) => outcome switch
    {
        "HttpSuccess" => "success", "HttpFailure" => "warning", "Failure" => "error", "Canceled" => "third", _ => "neutral"
    };

    private protected static string EndpointState(ServiceEndpointDiagnosticsSnapshot snapshot, ServiceEndpointSnapshot target) =>
        target.AttemptedAt is { } at && snapshot.CapturedAt - at > snapshot.Retention ? "Stale" : target.Resolution;

    private protected static bool EndpointAttention(ServiceEndpointDiagnosticsSnapshot snapshot, ServiceEndpointSnapshot target) =>
        EndpointState(snapshot, target) is "Failed" or "Timeout" or "Stale" or "Empty" ||
        target.Calls.Any(call => call.Outcome is "HttpFailure" or "Failure" or "Canceled");

    private protected static bool RetainedPrior(ServiceEndpointSnapshot target) =>
        target.Resolution is "Failed" or "Timeout" && target.ResolvedAt is not null && target.Candidates.Count != 0;

    private protected static double? P95(ServiceEndpointSnapshot target)
    {
        if (target.Calls.Count == 0) return null;
        return target.Calls.Select(call => call.DurationMs).Order().ElementAt((int)Math.Ceiling(target.Calls.Count * .95) - 1);
    }

    private protected static string Latency(double? ms) => ms?.ToString("F1", CultureInfo.CurrentCulture) ?? "\u2014";

    private protected static string RiskRole(string risk) => risk is "target-offline" or "target-faulted" ? "error" : "warning";
    private protected static string CoverageRole(string coverage) => coverage switch
    {
        "single" => "success", "shared" => "third", "unhosted" => "error", _ => "warning"
    };
}
