using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Monica.Framework.UI.UIObservableInstance.Support;

/// <summary>Reads and updates the workbench's shareable route, filters, and evidence selection.</summary>
public sealed class ObservableViewQuery
{
    internal const string MonitorPath = "/observable-instance-monitor";
    internal const string RegistryPath = MonitorPath + "/instances";
    internal const string GroupsPath = MonitorPath + "/groups";
    internal const string ExceptionsPath = MonitorPath + "/exceptions";
    private static readonly string[] Paths = [MonitorPath, RegistryPath, GroupsPath, ExceptionsPath];

    private readonly Dictionary<string, StringValues> _values;
    private readonly bool _exceptionRoute;

    internal ObservableViewQuery(NavigationManager navigation)
    {
        _exceptionRoute = navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0].TrimEnd('/').Equals(ExceptionsPath[1..], StringComparison.OrdinalIgnoreCase);
        _values = QueryHelpers.ParseQuery(navigation.ToAbsoluteUri(navigation.Uri).Query);
    }

    internal string? this[string key] => _values.TryGetValue(key, out var values) &&
        !string.IsNullOrWhiteSpace(values[0]) ? values[0] : null;
    internal string Search => this["q"]?.Trim() ?? "";
    internal string? Inspector => this["inspect"];
    internal string Timeline => this["tab"] switch
    {
        "exceptions" => "exceptions",
        "transitions" => "transitions",
        "timeline" or "history" => "timeline",
        "current" => "current",
        null when _exceptionRoute => "exceptions",
        _ => "current"
    };
    internal long? Sequence => long.TryParse(this["seq"], NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > 0
        ? seq : null;

    internal DateTime? Bound(string name) =>
        long.TryParse(this[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) &&
        unix is >= -62135596800000 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds(unix).UtcDateTime : null;

    internal static bool OwnsPath(NavigationManager navigation, string location)
    {
        var path = "/" + navigation.ToBaseRelativePath(location).Split('?', '#')[0].TrimEnd('/');
        return Paths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    internal static string Link(NavigationManager navigation, string path, params (string Key, string? Value)[] values) =>
        QueryHelpers.AddQueryString(navigation.ToAbsoluteUri(path.TrimStart('/')).ToString(),
            values.Where(item => item.Value is not null).Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)));

    internal static void Redirect(NavigationManager navigation, bool grouped)
    {
        var query = new ObservableViewQuery(navigation);
        var values = query._values.ToDictionary(item => item.Key, item => (string?)item.Value[0]);
        values["section"] = "registry";
        if (grouped) values["grouping"] = "group";
        navigation.NavigateTo(Link(navigation, MonitorPath, values.Select(pair => (pair.Key, pair.Value)).ToArray()),
            new NavigationOptions { ReplaceHistoryEntry = true });
    }

    internal static string Epoch(DateTime instant) => new DateTimeOffset(instant).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
}

/// <summary>Qualifies a timeline position by the tracker that owns its sequence.</summary>
/// <param name="Id">Registry instance ID.</param>
/// <param name="Tab">Initial evidence view, optionally including a timeline filter.</param>
/// <param name="Sequence">Position within this registration, when known.</param>
public sealed record ObservableInspection(string Id, string? Tab = null, long? Sequence = null);
