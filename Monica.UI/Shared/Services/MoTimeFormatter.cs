using System.Globalization;
using Microsoft.Extensions.Options;
using Monica.Core.Clock;
using Monica.Modules;

namespace Monica.UI.Shared.Services;

/// <summary>
/// Formats page timestamps in the deployment's Clock timezone (UTC when unset), independently of
/// the server and browser timezone. Registered as a host-owned singleton by <see cref="ModuleShellUI"/>.
/// Missing values render as an em dash. Wire data, exports and scheduling calculations retain their own contracts.
/// </summary>
/// <remarks>
/// UTC and Local <see cref="DateTime"/> values represent instants and are converted. Unspecified values
/// already represent deployment wall time, such as business audit fields, and remain unchanged.
/// An explicitly requested offset format requires that wall time to represent a valid DateTimeOffset.
/// A caller with a known UTC value whose Kind was lost must mark it as UTC before formatting.
/// Formatting uses the current UI culture on each call; formats are configured through
/// <see cref="ModuleShellUIOption.TimeDisplay"/>.
/// </remarks>
public sealed class MoTimeFormatter(
    IOptions<ModuleShellUIOption> shellOptions,
    IOptions<ModuleClockOption> clockOptions)
{
    /// <summary>Gets the deployment timezone, defaulting to UTC when Clock has no override.</summary>
    public TimeZoneInfo TimeZone => clockOptions.Value.LocalTimeZone?.GetTimeZoneInfo() ?? TimeZoneInfo.Utc;

    /// <summary>Converts an instant into the configured deployment timezone.</summary>
    public DateTimeOffset ToDisplayTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZone);

    /// <summary>Converts an instant, or preserves an unspecified deployment wall-clock value.</summary>
    public DateTime ToDisplayTime(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? value
        : ToDisplayTime(new DateTimeOffset(value)).DateTime;

    /// <summary>
    /// Formats an optional instant. A custom .NET format may be supplied for a precision-sensitive
    /// chart; ordinary page timestamps use the configured format.
    /// </summary>
    public string Format(DateTimeOffset? value, string? format = null) => value is { } timestamp
        ? ToDisplayTime(timestamp).ToString(format ?? shellOptions.Value.TimeDisplay.DateTimeFormat, CultureInfo.CurrentCulture)
        : "—";

    /// <summary>Formats an optional DateTime according to its documented Kind semantics.</summary>
    public string Format(DateTime? value, string? format = null) => value is { } timestamp
        ? timestamp.Kind == DateTimeKind.Unspecified
            ? FormatUnspecified(timestamp, format ?? shellOptions.Value.TimeDisplay.DateTimeFormat)
            : Format(new DateTimeOffset(timestamp), format)
        : "—";

    /// <summary>Formats the deployment date of an optional instant.</summary>
    public string FormatDate(DateTimeOffset? value) => Format(value, shellOptions.Value.TimeDisplay.DateFormat);

    /// <summary>Formats the deployment date of an optional DateTime.</summary>
    public string FormatDate(DateTime? value) => Format(value, shellOptions.Value.TimeDisplay.DateFormat);

    /// <summary>Formats the deployment time of an optional instant.</summary>
    public string FormatTime(DateTimeOffset? value) => Format(value, shellOptions.Value.TimeDisplay.TimeFormat);

    /// <summary>Formats the deployment time of an optional DateTime.</summary>
    public string FormatTime(DateTime? value) => Format(value, shellOptions.Value.TimeDisplay.TimeFormat);

    /// <summary>
    /// Formats an already projected wall time without timezone conversion, for a clearly labeled
    /// schedule editor whose calendar timezone can differ from Clock. Ordinary timestamps use <see cref="Format(DateTimeOffset?, string?)"/>.
    /// </summary>
    public string FormatWallTime(DateTimeOffset? value) => value is { } timestamp
        ? timestamp.ToString(shellOptions.Value.TimeDisplay.DateTimeFormat, CultureInfo.CurrentCulture)
        : "—";

    /// <summary>
    /// Formats a range, omitting the repeated end date when both endpoints share a deployment date.
    /// Ranges crossing an offset change include endpoint offsets so a repeated wall hour cannot
    /// appear reversed. The caller owns interval inclusion semantics and labels.
    /// </summary>
    public string FormatRange(DateTimeOffset start, DateTimeOffset end)
    {
        var localStart = ToDisplayTime(start);
        var localEnd = ToDisplayTime(end);
        var startText = Format(start);
        var endText = localStart.Date == localEnd.Date ? FormatTime(end) : Format(end);
        if (localStart.Offset != localEnd.Offset)
        {
            startText = IncludeOffset(startText, localStart);
            endText = IncludeOffset(endText, localEnd);
        }

        return $"{startText} → {endText}";
    }

    private static string IncludeOffset(string text, DateTimeOffset value)
    {
        var offset = value.ToString("zzz", CultureInfo.InvariantCulture);
        return text.Contains(offset, StringComparison.Ordinal) ? text : $"{text} {offset}";
    }

    private string FormatUnspecified(DateTime value, string format)
    {
        // Wall-time values, including date-only sentinels, need no representable UTC instant unless
        // the caller explicitly requests an offset. Do not let the OS supply that offset.
        return format is "o" or "O" || format.Contains('z') || format.Contains('K')
            ? new DateTimeOffset(value, TimeZone.GetUtcOffset(value)).ToString(format, CultureInfo.CurrentCulture)
            : value.ToString(format, CultureInfo.CurrentCulture);
    }
}
