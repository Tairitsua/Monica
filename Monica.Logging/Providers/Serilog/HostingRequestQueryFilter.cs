using Serilog.Events;

namespace Monica.Logging.Providers.Serilog;

/// <summary>Excludes query-bearing hosting events only for explicitly selected request paths.</summary>
internal static class HostingRequestQueryFilter
{
    private const string HOSTING_DIAGNOSTICS = "Microsoft.AspNetCore.Hosting.Diagnostics";

    public static bool Excludes(LogEvent logEvent, IReadOnlyCollection<string> paths)
    {
        if (GetScalarText(logEvent, "SourceContext") != HOSTING_DIAGNOSTICS
            || GetScalarText(logEvent, "RequestPath") is not { } requestPath
            || GetScalarText(logEvent, "QueryString") is not { Length: > 1 })
            return false;

        return paths.Any(prefix => requestPath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || requestPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetScalarText(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: { } scalar }
            ? scalar.ToString()
            : null;
}
