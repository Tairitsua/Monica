using System.Globalization;
using Microsoft.Extensions.Localization;
using Monica.Framework.Seeder.Models;

namespace Monica.Framework.UI.Localization;

/// <summary>
/// Provides localized display text for Seeder diagnostics values.
/// </summary>
public static class SeederResourceExtensions
{
    /// <summary>Gets localized text for a run status.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederRunStatus status) => status switch
    {
        SeederRunStatus.Waiting => localizer["RunStatus:Waiting"],
        SeederRunStatus.Running => localizer["RunStatus:Running"],
        SeederRunStatus.Aborting => localizer["RunStatus:Aborting"],
        SeederRunStatus.Succeeded => localizer["RunStatus:Succeeded"],
        SeederRunStatus.CompletedWithFailures => localizer["RunStatus:CompletedWithFailures"],
        SeederRunStatus.Aborted => localizer["RunStatus:Aborted"],
        SeederRunStatus.Cancelled => localizer["RunStatus:Cancelled"],
        SeederRunStatus.Held => localizer["RunStatus:Held"],
        _ => localizer["Common:Unknown"]
    };

    /// <summary>Gets localized text for a seeder status.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederStatus status) => status switch
    {
        SeederStatus.Pending => localizer["SeederStatus:Pending"],
        SeederStatus.Running => localizer["SeederStatus:Running"],
        SeederStatus.Succeeded => localizer["SeederStatus:Succeeded"],
        SeederStatus.Failed => localizer["SeederStatus:Failed"],
        SeederStatus.Blocked => localizer["SeederStatus:Blocked"],
        SeederStatus.Cancelled => localizer["SeederStatus:Cancelled"],
        SeederStatus.Held => localizer["SeederStatus:Held"],
        SeederStatus.Disabled => localizer["SeederStatus:Disabled"],
        _ => localizer["Common:Unknown"]
    };

    /// <summary>Gets localized text for an attempt status.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederAttemptStatus status) => status switch
    {
        SeederAttemptStatus.Running => localizer["AttemptStatus:Running"],
        SeederAttemptStatus.Succeeded => localizer["AttemptStatus:Succeeded"],
        SeederAttemptStatus.Failed => localizer["AttemptStatus:Failed"],
        SeederAttemptStatus.Cancelled => localizer["AttemptStatus:Cancelled"],
        _ => localizer["Common:Unknown"]
    };

    /// <summary>Gets localized text for a Seeder execution mode.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederExecutionMode mode) => mode switch
    {
        SeederExecutionMode.Inherit => localizer["ExecutionMode:Inherit"],
        SeederExecutionMode.Concurrent => localizer["ExecutionMode:Concurrent"],
        SeederExecutionMode.Exclusive => localizer["ExecutionMode:Exclusive"],
        _ => localizer["Common:Unknown"]
    };

    /// <summary>Gets localized text for a Seeder readiness criticality.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederCriticality criticality) =>
        criticality switch
        {
            SeederCriticality.Inherit => localizer["Criticality:Inherit"],
            SeederCriticality.Required => localizer["Criticality:Required"],
            SeederCriticality.Optional => localizer["Criticality:Optional"],
            _ => localizer["Common:Unknown"]
        };

    /// <summary>Gets localized text for a Seeder failure behavior.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederFailureBehavior behavior) =>
        behavior switch
        {
            SeederFailureBehavior.Inherit => localizer["FailureBehavior:Inherit"],
            SeederFailureBehavior.ContinueAndRecord => localizer["FailureBehavior:ContinueAndRecord"],
            SeederFailureBehavior.FailFast => localizer["FailureBehavior:FailFast"],
            _ => localizer["Common:Unknown"]
        };

    /// <summary>Gets localized text for the origin of an effective Seeder policy.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederPolicySource source) => source switch
    {
        SeederPolicySource.ModuleDefault => localizer["PolicySource:ModuleDefault"],
        SeederPolicySource.SeederOverride => localizer["PolicySource:SeederOverride"],
        _ => localizer["Common:Unknown"]
    };

    /// <summary>Gets localized text for the Seeder contribution to readiness.</summary>
    public static string GetText(this IStringLocalizer<SeederResource> localizer, SeederReadinessStatus status) =>
        status switch
        {
            SeederReadinessStatus.Healthy => localizer["ReadinessStatus:Healthy"],
            SeederReadinessStatus.Degraded => localizer["ReadinessStatus:Degraded"],
            SeederReadinessStatus.Unhealthy => localizer["ReadinessStatus:Unhealthy"],
            _ => localizer["Common:Unknown"]
        };

    /// <summary>Formats the completed and total Seeder counts with localized singular or plural grammar.</summary>
    public static string FormatSeederRunProgress(
        this IStringLocalizer<SeederResource> localizer,
        int completedCount,
        int totalCount) =>
        totalCount == 1
            ? localizer["Overview:ProgressSummary:Singular", completedCount, totalCount]
            : localizer["Overview:ProgressSummary:Plural", completedCount, totalCount];

    /// <summary>Formats the filtered and total Seeder counts with localized singular or plural grammar.</summary>
    public static string FormatSeederFilterResults(
        this IStringLocalizer<SeederResource> localizer,
        int filteredCount,
        int totalCount) =>
        totalCount == 1
            ? localizer["Filters:Results:Singular", filteredCount, totalCount]
            : localizer["Filters:Results:Plural", filteredCount, totalCount];

    /// <summary>Formats a dependency count with localized singular or plural grammar.</summary>
    public static string FormatSeederDependencyCount(
        this IStringLocalizer<SeederResource> localizer,
        int dependencyCount) =>
        dependencyCount == 1
            ? localizer["Table:DependencyCount:Singular", dependencyCount]
            : localizer["Table:DependencyCount:Plural", dependencyCount];

    /// <summary>Formats the started and maximum attempt counts with localized singular or plural grammar.</summary>
    public static string FormatSeederAttemptsSummary(
        this IStringLocalizer<SeederResource> localizer,
        int startedCount,
        int maximumCount) =>
        startedCount == 1
            ? localizer["Details:AttemptsSummary:Singular", startedCount, maximumCount]
            : localizer["Details:AttemptsSummary:Plural", startedCount, maximumCount];

    /// <summary>Formats a recorded attempt count with localized singular or plural grammar.</summary>
    public static string FormatSeederRecordedAttempts(
        this IStringLocalizer<SeederResource> localizer,
        int attemptCount) =>
        attemptCount == 1
            ? localizer["Details:Attempts:Count:Singular", attemptCount]
            : localizer["Details:Attempts:Count:Plural", attemptCount];

    /// <summary>Formats a duration using compact localized units.</summary>
    public static string FormatSeederDuration(this IStringLocalizer<SeederResource> localizer, TimeSpan? duration)
    {
        if (duration is null)
        {
            return localizer["Common:NotAvailable"];
        }

        var value = duration.Value < TimeSpan.Zero ? TimeSpan.Zero : duration.Value;
        if (value.TotalMilliseconds < 1000)
        {
            return localizer["Common:DurationMilliseconds", value.TotalMilliseconds];
        }

        if (value.TotalMinutes < 1)
        {
            return localizer["Common:DurationSeconds", value.TotalSeconds];
        }

        if (value.TotalHours < 1)
        {
            return localizer["Common:DurationMinutesSeconds", (int)value.TotalMinutes, value.Seconds];
        }

        return localizer["Common:DurationHoursMinutes", (int)value.TotalHours, value.Minutes];
    }

}
