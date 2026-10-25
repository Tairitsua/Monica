using System.Globalization;

namespace Monica.UI.UISystemInfo.Models;

/// <summary>
/// Represents the configured Clock display timezone captured for a system information response.
/// </summary>
public sealed record SystemTimeZone
{
    /// <summary>
    /// Gets the platform time zone identifier, such as <c>Asia/Shanghai</c> or <c>China Standard Time</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the UTC offset in effect at the instant when the system information was captured.
    /// </summary>
    public required TimeSpan UtcOffset { get; init; }

    /// <summary>
    /// Captures the local server timezone independently of the Clock display policy.
    /// </summary>
    /// <param name="instant">The instant used to resolve daylight-saving and other offset rules.</param>
    /// <returns>The captured local server time zone.</returns>
    public static SystemTimeZone CaptureLocal(DateTimeOffset instant)
    {
        var timeZone = TimeZoneInfo.Local;

        return new SystemTimeZone
        {
            Id = timeZone.Id,
            UtcOffset = timeZone.GetUtcOffset(instant)
        };
    }

    /// <summary>
    /// Formats the identifier and current offset for diagnostic display.
    /// </summary>
    /// <returns>A value such as <c>Asia/Shanghai (UTC+08:00)</c>.</returns>
    public string ToDisplayString()
    {
        var sign = UtcOffset < TimeSpan.Zero ? '-' : '+';
        var absoluteOffset = UtcOffset.Duration();
        var offsetText = absoluteOffset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        return $"{Id} (UTC{sign}{offsetText})";
    }
}
