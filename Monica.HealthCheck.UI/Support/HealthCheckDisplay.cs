using Microsoft.Extensions.Localization;
using Monica.HealthCheck.UI.Localization;
using Monica.Modules;
using Monica.UI.Shared.Components.Visual;
using Monica.HealthCheck.Models;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.HealthCheck.UI.Support;

/// <summary>
/// Maps health states to the shared MudBlazor semantic presentation contract.
/// </summary>
public static class HealthCheckDisplay
{
    /// <summary>Gets the semantic MudBlazor color for a health state.</summary>
    public static Color Color(HealthCheckState state) => state switch
    {
        HealthCheckState.Healthy => MudBlazor.Color.Success,
        HealthCheckState.Degraded => MudBlazor.Color.Warning,
        HealthCheckState.Unhealthy => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default
    };

    /// <summary>Gets the semantic MudBlazor alert severity for a health state.</summary>
    public static Severity Severity(HealthCheckState state) => state switch
    {
        HealthCheckState.Healthy => MudBlazor.Severity.Success,
        HealthCheckState.Degraded => MudBlazor.Severity.Warning,
        HealthCheckState.Unhealthy => MudBlazor.Severity.Error,
        _ => MudBlazor.Severity.Normal
    };

    /// <summary>Gets the shared Monica icon for a health state.</summary>
    public static string Icon(HealthCheckState state) => state switch
    {
        HealthCheckState.Healthy => MoIcons.Success,
        HealthCheckState.Degraded => MoIcons.Warning,
        HealthCheckState.Unhealthy => MoIcons.Error,
        _ => MoIcons.Help
    };

    internal static MoVisualRole Role(HealthCheckState state) => state switch
    {
        HealthCheckState.Healthy => MoVisualRole.Success,
        HealthCheckState.Degraded => MoVisualRole.Warning,
        HealthCheckState.Unhealthy => MoVisualRole.Error,
        _ => MoVisualRole.Neutral
    };

    internal static MoVisualRole TagRole(string tag) => tag.ToLowerInvariant() switch
    {
        "ready" => MoVisualRole.Primary,
        "live" => MoVisualRole.Info,
        "monica" => MoVisualRole.Secondary,
        _ => MoVisualRole.Neutral
    };

    internal static (MoVisualRole Role, string Label) Impact(HealthCheckEntrySnapshot entry,
        ModuleHealthCheckOption options, IStringLocalizer<HealthCheckResource> localizer)
    {
        var ready = options.EnableReadinessEndpoint && entry.Tags.Contains("ready", StringComparer.OrdinalIgnoreCase);
        var live = options.EnableLivenessEndpoint && entry.Tags.Contains("live", StringComparer.OrdinalIgnoreCase);
        if (!ready && !live)
            return (MoVisualRole.Neutral, localizer["Design:AuxiliaryLabel"]);

        var gate = (ready, live) switch
        {
            (true, true) => localizer["Gates:Both", options.ReadinessPath, options.LivenessPath].Value,
            (true, false) => options.ReadinessPath,
            _ => options.LivenessPath
        };

        return entry.Status switch
        {
            HealthCheckState.Unhealthy => (MoVisualRole.Error, localizer["Gates:Blocks", gate]),
            HealthCheckState.Degraded => (MoVisualRole.Warning, localizer["Gates:Degrades", gate]),
            _ when ready && live => (MoVisualRole.Primary, localizer["Gates:BothProbes"]),
            _ when ready => (MoVisualRole.Primary, $"{localizer["Design:ReadinessLabel"]} ({gate})"),
            _ => (MoVisualRole.Info, $"{localizer["Design:LivenessLabel"]} ({gate})")
        };
    }

    internal static double Share(TimeSpan check, TimeSpan wall) => wall > TimeSpan.Zero
        ? Math.Clamp(check / wall, 0, 1) : 0;

    internal static TimeSpan Wall(HealthCheckSnapshot snapshot) =>
        snapshot.Entries.Select(entry => entry.Duration).Append(snapshot.Duration).Max();

    internal static string ShareText(TimeSpan check, TimeSpan wall, IStringLocalizer<HealthCheckResource> localizer)
    {
        var share = Share(check, wall);
        return share is > 0 and < .01 ? localizer["Common:LessThanOnePercent"].Value
            : share >= .995 && check < wall ? .999.ToString("P1") : share.ToString("P0");
    }

    internal static string Duration(TimeSpan time, IStringLocalizer<HealthCheckResource> localizer)
    {
        var ms = time.TotalMilliseconds;
        return ms >= 1000 ? localizer["Common:DurationSeconds", time.TotalSeconds].Value :
            localizer["Common:DurationMilliseconds", ms.ToString(ms < 10 ? "N2" : ms < 100 ? "N1" : "N0")].Value;
    }

    internal static string BarWidth(double share, double minimum = 0) =>
        FormattableString.Invariant($"width:{(share > 0 ? Math.Max(minimum, share * 100) : 0):0.##}%");

    internal static int RedactedCount(HealthCheckEntrySnapshot entry) =>
        entry.Data.Values.Count(value => value == "[redacted]");
}
