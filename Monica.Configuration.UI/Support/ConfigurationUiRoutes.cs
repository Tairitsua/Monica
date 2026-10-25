using Microsoft.AspNetCore.WebUtilities;
using Monica.Configuration.Models;

namespace Monica.Configuration.UI.Support;

/// <summary>
/// Builds and parses Monica.Configuration.UI routes and query parameters.
/// </summary>
internal static class ConfigurationUiRoutes
{
    private const string DEFINITION_KEY_QUERY_KEY = "definitionKey";
    private const string PATH_QUERY_KEY = "path";

    /// <summary>
    /// Gets the configuration state route.
    /// </summary>
    public const string STATE_ROUTE = "/configuration/state";

    /// <summary>
    /// Gets the configuration history route.
    /// </summary>
    public const string HISTORY_ROUTE = "/configuration/history";

    /// <summary>
    /// Gets the configuration unified versions route.
    /// </summary>
    public const string VERSIONS_ROUTE = "/configuration/versions";

    /// <summary>
    /// Gets the configuration debug route.
    /// </summary>
    public const string DEBUG_ROUTE = "/configuration/debug";

    /// <summary>
    /// Gets the runtime configuration sources route.
    /// </summary>
    public const string SOURCES_ROUTE = "/configuration/sources";

    /// <summary>
    /// Gets the configuration store implementations route.
    /// </summary>
    public const string STORAGE_ROUTE = "/configuration/storage";

    /// <summary>
    /// Builds the state route for an optional target.
    /// </summary>
    /// <param name="definitionKey">Definition key target.</param>
    /// <param name="logicalPath">Logical path target.</param>
    /// <returns>The state route.</returns>
    public static string State(string? definitionKey = null, LogicalPath? logicalPath = null)
    {
        return WithPath(WithDefinitionKey(STATE_ROUTE, definitionKey), logicalPath);
    }

    /// <summary>
    /// Builds the history route for an optional target.
    /// </summary>
    /// <param name="definitionKey">Definition key filter.</param>
    /// <param name="logicalPath">Logical path filter.</param>
    /// <returns>The history route.</returns>
    public static string History(string? definitionKey = null, LogicalPath? logicalPath = null)
    {
        return WithPath(WithDefinitionKey(HISTORY_ROUTE, definitionKey), logicalPath);
    }

    /// <summary>
    /// Reads the definition key query parameter from the current URI.
    /// </summary>
    public static string? ReadDefinitionKey(string absoluteUri)
    {
        var uri = new Uri(absoluteUri, UriKind.Absolute);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue(DEFINITION_KEY_QUERY_KEY, out var values) || values.Count == 0)
        {
            return null;
        }

        var definitionKey = values[0];
        return string.IsNullOrWhiteSpace(definitionKey) ? null : definitionKey;
    }

    /// <summary>
    /// Reads the logical path query parameter from the current URI.
    /// </summary>
    public static LogicalPath ReadPath(string absoluteUri)
    {
        var uri = new Uri(absoluteUri, UriKind.Absolute);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue(PATH_QUERY_KEY, out var values) || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
        {
            return LogicalPath.Root;
        }

        return LogicalPath.Parse(values[0]!);
    }

    /// <summary>
    /// Builds a route with an optional logical path query parameter.
    /// </summary>
    public static string WithPath(string route, LogicalPath? logicalPath)
    {
        if (logicalPath is null || logicalPath.Depth == 0)
        {
            return route;
        }

        return QueryHelpers.AddQueryString(route, PATH_QUERY_KEY, logicalPath.ToCanonicalString());
    }

    private static string WithDefinitionKey(string route, string? definitionKey)
    {
        return string.IsNullOrWhiteSpace(definitionKey)
            ? route
            : QueryHelpers.AddQueryString(route, DEFINITION_KEY_QUERY_KEY, definitionKey);
    }
}
