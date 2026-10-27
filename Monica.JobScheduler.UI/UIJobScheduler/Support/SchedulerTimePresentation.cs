using System.Globalization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.UI.Localization;
using Monica.Modules;
using Monica.UI.Shared.Services;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>
/// Presents instants through the shared UI clock and owns scheduler-specific calendar conversions and durations.
/// </summary>
/// <remarks>
/// Persisted instants remain UTC. Ordinary timestamps use <see cref="MoTimeFormatter" /> and the deployment's
/// Clock timezone. Calendar filters and wall-clock inputs retain <see cref="ModuleJobSchedulerOption.CronTimeZone" />,
/// independently of display formatting or the server's operating-system timezone.
/// </remarks>
internal sealed class SchedulerTimePresentation(
    IOptions<ModuleJobSchedulerOption> schedulerOptions,
    IStringLocalizer<JobSchedulerResource> localizer,
    MoTimeFormatter timeFormatter)
{
    private readonly TimeZoneInfo _timeZone = schedulerOptions.Value.CronTimeZone;

    /// <summary>
    /// Gets the configured scheduler timezone identifier shown once by the shared workspace.
    /// </summary>
    internal string TimeZoneId => _timeZone.Id;

    /// <summary>
    /// Gets the platform-provided descriptive timezone name for supporting UI metadata.
    /// </summary>
    internal string TimeZoneDisplayName => _timeZone.DisplayName;

    /// <summary>
    /// Gets the UTC instant at which the configured scheduler calendar day begins.
    /// </summary>
    /// <remarks>
    /// A rare daylight-saving transition can skip or repeat midnight. Skipped wall times advance to the first valid
    /// minute, while repeated wall times select the earliest represented instant so the complete scheduler day remains
    /// inside the analytics range.
    /// </remarks>
    internal DateTimeOffset GetStartOfSchedulerDayUtc(DateTimeOffset value) =>
        GetStartOfSchedulerCalendarDayUtc(ToSchedulerWallTime(value).Date);

    /// <summary>Resolves the beginning of a supplied scheduler-calendar date without interpreting it as a UTC instant.</summary>
    /// <remarks>Skipped midnight advances to the first valid minute; repeated midnight selects its earliest instant.</remarks>
    internal DateTimeOffset GetStartOfSchedulerCalendarDayUtc(DateTime calendarDate)
    {
        var wallTime = DateTime.SpecifyKind(calendarDate.Date, DateTimeKind.Unspecified);
        while (_timeZone.IsInvalidTime(wallTime))
        {
            wallTime = wallTime.AddMinutes(1);
        }

        if (_timeZone.IsAmbiguousTime(wallTime))
        {
            return _timeZone.GetAmbiguousTimeOffsets(wallTime)
                .Select(offset => new DateTimeOffset(wallTime, offset).ToUniversalTime())
                .Min();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(wallTime, _timeZone), TimeSpan.Zero);
    }

    /// <summary>
    /// Formats one durable instant using the deployment's shared UI time policy.
    /// </summary>
    internal string FormatTimestamp(DateTimeOffset? value) => timeFormatter.Format(value);

    /// <summary>Formats execution-detail evidence with three millisecond digits in the deployment Clock zone.</summary>
    internal string FormatExecutionTimestamp(DateTimeOffset? value) => timeFormatter.Format(value, "yyyy-MM-dd HH:mm:ss.fff");

    /// <summary>Formats the compact execution-history clock with three millisecond digits.</summary>
    internal string FormatExecutionClock(DateTimeOffset? value) => timeFormatter.Format(value, "HH:mm:ss.fff");

    /// <summary>Formats a compact ordinary observation time through the host-owned display policy.</summary>
    internal string FormatClock(DateTimeOffset? value) => timeFormatter.FormatTime(value);

    /// <summary>Formats a compact chart boundary in the host display timezone, without milliseconds.</summary>
    internal string FormatChartTimestamp(DateTimeOffset value) => timeFormatter.Format(value, "MM-dd HH:mm");

    /// <summary>Uses the host clock on the observation day and retains a date for records on other display days.</summary>
    internal string FormatListTimestamp(DateTimeOffset value, DateTimeOffset observedAtUtc) =>
        timeFormatter.ToDisplayTime(value).Date == timeFormatter.ToDisplayTime(observedAtUtc).Date
            ? timeFormatter.FormatTime(value) : timeFormatter.Format(value);

    /// <summary>Formats an explicitly projected schedule-calendar instant without applying the deployment Clock zone again.</summary>
    internal string FormatScheduleTimestamp(DateTimeOffset value) => timeFormatter.FormatWallTime(TimeZoneInfo.ConvertTime(value, _timeZone));

    /// <summary>Formats exact elapsed execution evidence; ordinary list durations retain their compact scale.</summary>
    internal string FormatPreciseDuration(TimeSpan? duration)
    {
        var parts = PreciseDurationParts(duration);
        return string.IsNullOrEmpty(parts.Unit) ? parts.Value : $"{parts.Value} {parts.Unit}";
    }

    /// <summary>Separates exact elapsed evidence from its localized unit for the drawer's display-scale reading.</summary>
    internal (string Value, string Unit) PreciseDurationParts(TimeSpan? duration)
    {
        if (duration is null) return ("—", string.Empty);
        var value = TimeSpan.FromMilliseconds(Math.Max(0, Math.Round(duration.Value.TotalMilliseconds)));
        if (value.TotalMinutes < 1) return (value.TotalSeconds.ToString("F3", CultureInfo.CurrentCulture), localizer["Common:Duration:Seconds", ""].Value.Trim());
        var prefix = value.TotalHours >= 1
            ? $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}"
            : $"{value.Minutes}:{value.Seconds:00}.{value.Milliseconds:000}";
        return (prefix, localizer[value.TotalHours >= 1 ? "Workspace:ClockHours" : "Workspace:ClockMinutes"]);
    }

    /// <summary>Formats the recorded duration of the selected execution at the accepted observation instant.</summary>
    internal string FormatPreciseExecutionDuration(JobExecutionInstance execution, DateTimeOffset observedAtUtc) =>
        FormatPreciseDuration(GetExecutionDuration(execution, observedAtUtc));

    /// <summary>Formats an interval without repeating its date when both endpoints share a display-calendar day.</summary>
    internal string FormatRange(DateTimeOffset start, DateTimeOffset end) => timeFormatter.FormatRange(start, end);

    /// <summary>
    /// Projects an absolute instant into the configured scheduler-zone wall clock.
    /// </summary>
    internal DateTime ToSchedulerWallTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, _timeZone).DateTime;

    /// <summary>
    /// Interprets an unspecified wall-clock value in the configured scheduler timezone and returns its UTC instant.
    /// </summary>
    internal DateTimeOffset ConvertSchedulerWallTimeToUtc(DateTime wallTime)
    {
        var unspecifiedWallTime = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
        var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(unspecifiedWallTime, _timeZone);
        return new DateTimeOffset(utcDateTime, TimeSpan.Zero);
    }

    /// <summary>
    /// Tries to interpret a scheduler wall time, rejecting clock values skipped by a daylight-saving transition.
    /// </summary>
    internal bool TryConvertSchedulerWallTimeToUtc(DateTime wallTime, out DateTimeOffset utcValue)
    {
        var unspecifiedWallTime = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
        if (_timeZone.IsInvalidTime(unspecifiedWallTime))
        {
            utcValue = default;
            return false;
        }

        utcValue = ConvertSchedulerWallTimeToUtc(unspecifiedWallTime);
        return true;
    }

    /// <summary>
    /// Formats a compact analytics-axis label after projecting the fixed UTC bucket into the shared display timezone.
    /// </summary>
    internal string FormatBucketLabel(
        DateTimeOffset value,
        JobExecutionAnalyticsBucketSize bucketSize)
    {
        var schedulerTime = timeFormatter.ToDisplayTime(value);
        return bucketSize switch
        {
            JobExecutionAnalyticsBucketSize.Hour => schedulerTime.ToString("HH:mm", CultureInfo.CurrentCulture),
            JobExecutionAnalyticsBucketSize.Day => schedulerTime.ToString("MM-dd", CultureInfo.CurrentCulture),
            _ => FormatTimestamp(value)
        };
    }

    /// <summary>
    /// Formats elapsed time as milliseconds below one second and decimal seconds otherwise.
    /// </summary>
    internal string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "—";
        }

        var value = duration.Value < TimeSpan.Zero ? TimeSpan.Zero : duration.Value;
        if (value.TotalSeconds >= 1)
        {
            return FormatDurationUnit("Common:Duration:Seconds", value.TotalSeconds, "F2");
        }

        return FormatDurationUnit(
            "Common:Duration:Milliseconds",
            Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero),
            "F0");
    }

    /// <summary>
    /// Calculates and formats the elapsed time represented by one durable execution snapshot.
    /// </summary>
    internal string FormatExecutionDuration(JobExecutionInstance execution, DateTimeOffset observedAtUtc)
    {
        var duration = GetExecutionDuration(execution, observedAtUtc);
        if (duration is null)
        {
            return "—";
        }

        var formatted = FormatDuration(duration);
        return execution.State == JobExecutionState.Running && execution.CompletedAtUtc is null
            ? localizer["Common:RunningDuration", formatted]
            : formatted;
    }

    /// <summary>
    /// Calculates a durable attempt interval, using the observation instant only for currently running work.
    /// </summary>
    /// <remarks>
    /// A queued retry can retain the prior attempt's start without its completion. That evidence does not describe
    /// active elapsed work, so queued and incomplete terminal intervals remain unavailable. Terminal intervals use
    /// the durable start and terminal completion; they are not CPU time or the sum of every retry attempt.
    /// </remarks>
    internal static TimeSpan? GetExecutionDuration(
        JobExecutionInstance execution,
        DateTimeOffset observedAtUtc)
    {
        if (execution.StartedAtUtc is not { } startedAtUtc)
        {
            return null;
        }

        DateTimeOffset end;
        if (execution.State == JobExecutionState.Running && execution.CompletedAtUtc is null) end = observedAtUtc;
        else if (execution.IsTerminal && execution.CompletedAtUtc is { } completedAtUtc) end = completedAtUtc;
        else return null;
        return end < startedAtUtc ? TimeSpan.Zero : end - startedAtUtc;
    }

    private string FormatDurationUnit(string key, double value, string format) =>
        localizer[key, value.ToString(format, CultureInfo.CurrentCulture)];
}
