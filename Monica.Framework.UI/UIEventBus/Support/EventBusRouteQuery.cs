using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace Monica.Framework.UI.UIEventBus.Support;

internal sealed class EventBusRouteQuery
{
    private readonly Dictionary<string, string?> _query;

    internal EventBusRouteQuery(string query)
    {
        _query = QueryHelpers.ParseQuery(query).ToDictionary(pair => pair.Key,
            pair => (string?)pair.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);
    }

    internal string? this[string key] => _query.GetValueOrDefault(key);
    internal bool IsSet(string key) => this[key] == "1";

    internal int Page => int.TryParse(this["page"], NumberStyles.None, CultureInfo.InvariantCulture, out var page)
        ? Math.Max(1, page) : 1;

    internal T? Choice<T>(string key) where T : struct, Enum =>
        Enum.TryParse<T>(this[key], out var value) && Enum.IsDefined(value) ? value : null;

    internal string Link(string route, params (string Key, string? Value)[] edits) => Build(route, _query, edits);

    internal static string To(string route, params (string Key, string? Value)[] edits) => Build(route, null, edits);

    private static string Build(string route, Dictionary<string, string?>? current, (string Key, string? Value)[] edits)
    {
        var query = current is null ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) :
            new Dictionary<string, string?>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in edits)
        {
            if (value is null) query.Remove(key);
            else query[key] = value;
        }
        return QueryHelpers.AddQueryString(route, query);
    }
}
