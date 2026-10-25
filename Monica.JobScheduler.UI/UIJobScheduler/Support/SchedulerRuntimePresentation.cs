using Microsoft.Extensions.Localization;
using Monica.JobScheduler.UI.Localization;
using System.Globalization;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>
/// Formats captured runtime values and classifies owner observations against their actual capture.
/// </summary>
internal static class SchedulerRuntimePresentation
{
    /// <summary>
    /// Formats one interval with the largest natural unit from days down to milliseconds.
    /// </summary>
    internal static string FormatInterval(TimeSpan value, IStringLocalizer<JobSchedulerResource> localizer)
    {
        if (value >= TimeSpan.FromDays(1))
        {
            return Format(localizer["Runtime:Format:Days"], value.TotalDays);
        }

        if (value >= TimeSpan.FromHours(1))
        {
            return Format(localizer["Runtime:Format:Hours"], value.TotalHours);
        }

        if (value >= TimeSpan.FromMinutes(1))
        {
            return Format(localizer["Runtime:Format:Minutes"], value.TotalMinutes);
        }

        if (value >= TimeSpan.FromSeconds(1))
        {
            return Format(localizer["Runtime:Format:Seconds"], value.TotalSeconds);
        }

        return Format(localizer["Runtime:Format:Milliseconds"], value.TotalMilliseconds);
    }

    /// <summary>
    /// Preserves every tick of an interval in invariant constant format for on-demand inspection.
    /// </summary>
    internal static string FormatExactInterval(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    /// <summary>
    /// Classifies a definition observation without consulting a live clock or interpreting it as process health.
    /// </summary>
    /// <remarks>
    /// An observation after the capture is explicitly ahead of capture. Otherwise the threshold is inclusive:
    /// an observation exactly as old as <paramref name="staleAfter"/> is recent, and an older observation is stale.
    /// </remarks>
    internal static SchedulerOwnerObservationFreshness ClassifyOwnerObservation(
        DateTimeOffset capturedAtUtc,
        DateTimeOffset lastObservedAtUtc,
        TimeSpan staleAfter)
    {
        if (lastObservedAtUtc > capturedAtUtc)
        {
            return SchedulerOwnerObservationFreshness.AheadOfCapture;
        }

        return capturedAtUtc - lastObservedAtUtc > staleAfter
            ? SchedulerOwnerObservationFreshness.Stale
            : SchedulerOwnerObservationFreshness.Recent;
    }

    private static string Format(string pattern, double value) =>
        string.Format(pattern, value.ToString("0.##", CultureInfo.CurrentCulture));
}

/// <summary>Describes one definition observation relative to the accepted runtime capture.</summary>
internal enum SchedulerOwnerObservationFreshness
{
    /// <summary>The observation is at or within the supplied staleness threshold.</summary>
    Recent,

    /// <summary>The observation is older than the supplied threshold.</summary>
    Stale,

    /// <summary>The observation is later than the capture and must not be presented as healthy recency.</summary>
    AheadOfCapture
}
