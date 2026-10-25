namespace Monica.Configuration.UI.Support;

/// <summary>Admits inclusive calendar-date filters in the same deployment timezone used by page timestamps.</summary>
internal static class ConfigurationCalendarRange
{
    /// <summary>
    /// Ignores picker DateTime Kind and time-of-day: dates select deployment midnight through the final tick
    /// of the end date. Rejects missing or repeated DST boundaries instead of silently choosing an instant.
    /// </summary>
    internal static (DateTimeOffset? From, DateTimeOffset? To) Create(
        DateTime? fromDate, DateTime? toDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var from = fromDate is { } start ? Boundary(start.Date, timeZone) : (DateTimeOffset?)null;
        var to = toDate is { } end
            ? Boundary(end.Date.AddTicks(TimeSpan.TicksPerDay - 1), timeZone)
            : (DateTimeOffset?)null;
        if (from > to)
            throw new ConfigurationCalendarRangeException(ConfigurationCalendarRangeError.Reversed);
        return (from, to);
    }

    private static DateTimeOffset Boundary(DateTime value, TimeZoneInfo timeZone)
    {
        var wallTime = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(wallTime))
            throw new ConfigurationCalendarRangeException(ConfigurationCalendarRangeError.MissingBoundary);
        if (timeZone.IsAmbiguousTime(wallTime))
            throw new ConfigurationCalendarRangeException(ConfigurationCalendarRangeError.RepeatedBoundary);
        try
        {
            return new DateTimeOffset(wallTime, timeZone.GetUtcOffset(wallTime));
        }
        catch (ArgumentException)
        {
            throw new ConfigurationCalendarRangeException(ConfigurationCalendarRangeError.MissingBoundary);
        }
    }
}

internal enum ConfigurationCalendarRangeError { Reversed, MissingBoundary, RepeatedBoundary }

/// <summary>Carries a typed calendar-admission failure so the page can render its localized explanation.</summary>
internal sealed class ConfigurationCalendarRangeException(ConfigurationCalendarRangeError error)
    : ArgumentException($"The configured timezone cannot admit this calendar range: {error}.")
{
    internal ConfigurationCalendarRangeError Error { get; } = error;
}
