using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.Tool.Extensions;

namespace Monica.Framework.UI.Localization;

/// <summary>
/// Provides display text helpers for observable-instance UI localization.
/// </summary>
public static class ObservableInstanceLocalizationExtensions
{
    /// <summary>
    /// Gets the localized display text for an observable instance type.
    /// </summary>
    public static string GetInstanceTypeText(this IStringLocalizer localizer, ObservableInstanceViewModel instance)
    {
        return instance.InstanceType?.GetCleanName() ?? localizer["Shared:Labels:Unknown"].Value;
    }

    /// <summary>
    /// Gets the localized display text for an observable instance group.
    /// </summary>
    public static string GetGroupIdText(this IStringLocalizer localizer, ObservableInstanceViewModel instance)
    {
        return string.IsNullOrEmpty(instance.GroupId)
            ? localizer["Shared:Labels:Ungrouped"].Value
            : instance.GroupId;
    }

    /// <summary>
    /// Gets localized display text for a health state.
    /// </summary>
    public static string GetHealthStateText(this IStringLocalizer localizer, HealthState state)
    {
        return state switch
        {
            HealthState.Healthy => localizer["Shared:HealthStates:Healthy"].Value,
            HealthState.Unhealthy => localizer["Shared:HealthStates:Unhealthy"].Value,
            _ => localizer["Shared:HealthStates:Unknown"].Value
        };
    }

    /// <summary>
    /// Gets localized display text for a log level.
    /// </summary>
    public static string GetLogLevelText(this IStringLocalizer localizer, LogLevel? level)
    {
        return level switch
        {
            LogLevel.Trace => localizer["Shared:LogLevels:Trace"].Value,
            LogLevel.Debug => localizer["Shared:LogLevels:Debug"].Value,
            LogLevel.Information => localizer["Shared:LogLevels:Information"].Value,
            LogLevel.Warning => localizer["Shared:LogLevels:Warning"].Value,
            LogLevel.Error => localizer["Shared:LogLevels:Error"].Value,
            LogLevel.Critical => localizer["Shared:LogLevels:Critical"].Value,
            LogLevel.None => localizer["Shared:LogLevels:None"].Value,
            null => localizer["Shared:LogLevels:Unknown"].Value,
            _ => localizer["Shared:LogLevels:Unknown"].Value
        };
    }


    /// <summary>
    /// Gets localized display text for an exception status.
    /// </summary>
    public static string GetExceptionStatusText(this IStringLocalizer localizer, bool hasExceptions, int exceptionCount)
    {
        return hasExceptions
            ? localizer["Shared:ExceptionStatus:Count", exceptionCount].Value
            : localizer["Shared:ExceptionStatus:Normal"].Value;
    }

    /// <summary>
    /// Gets localized display text for an observable exception badge.
    /// </summary>
    public static string GetExceptionStatusText(this IStringLocalizer localizer, ObservableInstanceViewModel instance)
    {
        return localizer.GetExceptionStatusText(instance.HasExceptions, instance.ExceptionCount);
    }

    /// <summary>
    /// Gets localized display text for a state-history entry type.
    /// </summary>
    public static string GetEntryTypeDescription(this IStringLocalizer localizer, ObservableStateHistoryViewModel entry)
    {
        return (entry.IsException, entry.IsStateTransition) switch
        {
            (true, true) => localizer["Shared:EntryTypes:StateChangeWithException"].Value,
            (true, false) => localizer["Shared:EntryTypes:Exception"].Value,
            (false, true) => localizer["Shared:EntryTypes:StateChange"].Value,
            (false, false) => localizer["Shared:EntryTypes:Message"].Value
        };
    }

    /// <summary>
    /// Formats a relative timestamp with localized units.
    /// </summary>
    public static string FormatRelativeTime(this IStringLocalizer localizer, DateTime timestamp, Monica.UI.Shared.Services.MoTimeFormatter time)
    {
        var elapsed = DateTime.UtcNow - timestamp;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 60)
            return localizer["Shared:RelativeTime:SecondsAgo", Math.Max(0, (int)elapsed.TotalSeconds)].Value;
        if (elapsed.TotalMinutes < 60)
            return localizer["Shared:RelativeTime:MinutesAgo", (int)elapsed.TotalMinutes].Value;
        if (elapsed.TotalHours < 24)
            return localizer["Shared:RelativeTime:HoursAgo", (int)elapsed.TotalHours].Value;
        if (elapsed.TotalDays < 7)
            return localizer["Shared:RelativeTime:DaysAgo", (int)elapsed.TotalDays].Value;

        return time.Format(timestamp);
    }

    /// <summary>
    /// Formats a duration with localized units.
    /// </summary>
    public static string FormatDuration(this IStringLocalizer localizer, TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return localizer["Shared:Duration:DaysHours", (int)duration.TotalDays, duration.Hours].Value;
        if (duration.TotalHours >= 1)
            return localizer["Shared:Duration:HoursMinutes", (int)duration.TotalHours, duration.Minutes].Value;
        if (duration.TotalMinutes >= 1)
            return localizer["Shared:Duration:MinutesSeconds", (int)duration.TotalMinutes, duration.Seconds].Value;

        return localizer["Shared:Duration:Seconds", Math.Max(0, (int)duration.TotalSeconds)].Value;
    }
}
