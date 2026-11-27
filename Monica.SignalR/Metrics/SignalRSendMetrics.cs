using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.SignalR.Models;

namespace Monica.SignalR.Metrics;

/// <summary>
/// Owns Monica-observed SignalR send metrics and the app-owned snapshot state used by the debug UI.
/// </summary>
public sealed class SignalRSendMetrics
{
    private const string HUB_TAG_NAME = "signalr.hub";
    private const string METHOD_TAG_NAME = "signalr.method";
    private const string TARGET_KIND_TAG_NAME = "target.kind";
    private const string TARGET_ID_TAG_NAME = "signalr.target.id";
    private const string EVENT_TAG_NAME = "event";
    private const string ERROR_TYPE_TAG_NAME = "error.type";
    private const string EVENT_STARTED = "started";
    private const string EVENT_COMPLETED = "completed";
    private const string EVENT_FAILED = "failed";

    private readonly ConcurrentDictionary<SignalRSendMetricKey, SignalRSendMetric> _metrics = new();
    private readonly Counter<long> _sendEvents;
    private readonly Histogram<double> _sendDuration;
    private readonly IOptions<ModuleSignalROption> _options;

    /// <summary>
    /// Initializes SignalR send instruments.
    /// </summary>
    public SignalRSendMetrics(IMeterFactory meterFactory, IOptions<ModuleSignalROption> options)
    {
        _options = options;

        var meter = meterFactory.Create(SignalRMetricNames.MeterName);

        _sendEvents = meter.CreateCounter<long>(
            SignalRMetricNames.HubSendEvents,
            unit: "events",
            description: "Monica-observed SignalR server-to-client send lifecycle events.");

        _sendDuration = meter.CreateHistogram<double>(
            SignalRMetricNames.HubSendDuration,
            unit: "s",
            description: "Monica-observed SignalR server-to-client send duration.");

        meter.CreateObservableGauge(
            SignalRMetricNames.HubSendPending,
            ObservePendingSends,
            unit: "sends",
            description: "Current pending Monica-observed SignalR server-to-client sends.");
    }

    /// <summary>
    /// Gets a value indicating whether SignalR send metrics are enabled.
    /// </summary>
    public bool IsEnabled => _options.Value.EnableSendMetrics;

    /// <summary>
    /// Gets a value indicating whether target identifiers should be retained in diagnostics snapshots and emitted as tags.
    /// </summary>
    public bool IncludeTargetIdentifiers => _options.Value.IncludeSendDiagnosticTargetIdentifiers;

    /// <summary>
    /// Starts an observed send operation.
    /// </summary>
    internal SignalRSendMetricOperation StartObservedSend(string hubName, string methodName, SignalRSendTarget target)
    {
        if (!IsEnabled)
        {
            return SignalRSendMetricOperation.Disabled;
        }

        var key = new SignalRSendMetricKey(
            hubName,
            methodName,
            target.Kind,
            target.TargetCount,
            BuildTargetIdentifierKey(target.Identifiers));

        // Display names ride along for diagnostics only; the metric row keeps the identity derived from identifiers,
        // so a target does not split into a second row when the resolved display names change between sends.
        var metric = _metrics.GetOrAdd(key, _ => new SignalRSendMetric(key, target.Identifiers, target.IdentifierDisplayNames));
        var startedAt = metric.Start();
        RecordSendEvent(EVENT_STARTED, key, target);

        return new SignalRSendMetricOperation(this, metric, key, target, startedAt);
    }

    /// <summary>
    /// Returns the current diagnostics snapshot.
    /// </summary>
    public SignalRSendDiagnosticsSnapshot GetSnapshot()
    {
        var metrics = _metrics.Values
            .Select(metric => metric.CreateSnapshot())
            .OrderByDescending(metric => metric.PendingSendCount)
            .ThenByDescending(metric => metric.MaxDurationMilliseconds)
            .ThenBy(metric => metric.HubName, StringComparer.Ordinal)
            .ThenBy(metric => metric.MethodName, StringComparer.Ordinal)
            .ToList();

        return new SignalRSendDiagnosticsSnapshot
        {
            IsEnabled = IsEnabled,
            IncludesTargetIdentifiers = IncludeTargetIdentifiers,
            PendingSendCount = metrics.Sum(metric => metric.PendingSendCount),
            TotalStartedCount = metrics.Sum(metric => metric.StartedCount),
            TotalCompletedCount = metrics.Sum(metric => metric.CompletedCount),
            TotalFailedCount = metrics.Sum(metric => metric.FailedCount),
            MethodMetrics = CreateMethodMetrics(metrics),
            Metrics = metrics
        };
    }

    internal void CompleteObservedSend(
        SignalRSendMetric metric,
        SignalRSendMetricKey key,
        SignalRSendTarget target,
        long startedAt,
        Exception? exception)
    {
        var failed = exception is not null;
        var elapsed = metric.Finish(startedAt, failed);

        RecordSendEvent(failed ? EVENT_FAILED : EVENT_COMPLETED, key, target);
        RecordSendDuration(elapsed, key, target, exception);
    }

    private Measurement<long>[] ObservePendingSends()
    {
        return _metrics
            .Select(pair => new
            {
                pair.Key,
                PendingSendCount = pair.Value.GetPendingSendCount()
            })
            .Where(metric => metric.PendingSendCount != 0)
            .Select(metric => new Measurement<long>(
                metric.PendingSendCount,
                new KeyValuePair<string, object?>(HUB_TAG_NAME, metric.Key.HubName),
                new KeyValuePair<string, object?>(METHOD_TAG_NAME, metric.Key.MethodName),
                new KeyValuePair<string, object?>(TARGET_KIND_TAG_NAME, FormatTargetKind(metric.Key.TargetKind))))
            .ToArray();
    }

    private void RecordSendEvent(string eventName, SignalRSendMetricKey key, SignalRSendTarget target)
    {
        var tags = CreateSendTags(key, target);
        tags.Add(EVENT_TAG_NAME, eventName);
        _sendEvents.Add(1, tags);
    }

    private void RecordSendDuration(
        TimeSpan elapsed,
        SignalRSendMetricKey key,
        SignalRSendTarget target,
        Exception? exception)
    {
        var tags = CreateSendTags(key, target);
        if (exception is not null)
        {
            tags.Add(ERROR_TYPE_TAG_NAME, exception.GetType().Name);
        }

        _sendDuration.Record(elapsed.TotalSeconds, tags);
    }

    private TagList CreateSendTags(SignalRSendMetricKey key, SignalRSendTarget target)
    {
        var tags = new TagList
        {
            { HUB_TAG_NAME, key.HubName },
            { METHOD_TAG_NAME, key.MethodName },
            { TARGET_KIND_TAG_NAME, FormatTargetKind(key.TargetKind) }
        };

        if (IncludeTargetIdentifiers && target.Identifiers.Count > 0)
        {
            tags.Add(TARGET_ID_TAG_NAME, BuildTargetIdentifierKey(target.Identifiers));
        }

        return tags;
    }

    private static string BuildTargetIdentifierKey(IReadOnlyList<string> identifiers)
    {
        return identifiers.Count == 0
            ? string.Empty
            : string.Join('\u001f', identifiers.Order(StringComparer.Ordinal));
    }

    private static string FormatTargetKind(SignalRSendTargetKind targetKind)
    {
        return targetKind.ToString().ToLowerInvariant();
    }

    private static List<SignalRSendMethodMetricInfo> CreateMethodMetrics(IReadOnlyList<SignalRSendMetricInfo> metrics)
    {
        return metrics
            .GroupBy(metric => new { metric.HubName, metric.MethodName })
            .Select(group => CreateMethodMetric(group.Key.HubName, group.Key.MethodName, group.ToList()))
            .OrderByDescending(metric => metric.PendingSendCount)
            .ThenByDescending(metric => metric.MaxDurationMilliseconds)
            .ThenBy(metric => metric.HubName, StringComparer.Ordinal)
            .ThenBy(metric => metric.MethodName, StringComparer.Ordinal)
            .ToList();
    }

    private static SignalRSendMethodMetricInfo CreateMethodMetric(
        string hubName,
        string methodName,
        List<SignalRSendMetricInfo> targetMetrics)
    {
        var lastStartedMetric = targetMetrics
            .Where(metric => metric.LastStartedAtUtc.HasValue)
            .MaxBy(metric => metric.LastStartedAtUtc);
        var lastCompletedMetric = targetMetrics
            .Where(metric => metric.LastCompletedAtUtc.HasValue)
            .MaxBy(metric => metric.LastCompletedAtUtc);
        var finishedCount = targetMetrics.Sum(metric => metric.CompletedCount + metric.FailedCount);
        var totalAverageDuration = targetMetrics.Sum(metric =>
            metric.AverageDurationMilliseconds * (metric.CompletedCount + metric.FailedCount));

        return new SignalRSendMethodMetricInfo
        {
            HubName = hubName,
            MethodName = methodName,
            TargetMetricCount = targetMetrics.Count,
            PendingSendCount = targetMetrics.Sum(metric => metric.PendingSendCount),
            StartedCount = targetMetrics.Sum(metric => metric.StartedCount),
            CompletedCount = targetMetrics.Sum(metric => metric.CompletedCount),
            FailedCount = targetMetrics.Sum(metric => metric.FailedCount),
            LastDurationMilliseconds = lastCompletedMetric?.LastDurationMilliseconds ?? 0,
            MaxDurationMilliseconds = targetMetrics.Max(metric => metric.MaxDurationMilliseconds),
            AverageDurationMilliseconds = finishedCount == 0 ? 0 : totalAverageDuration / finishedCount,
            LastStartedAtUtc = lastStartedMetric?.LastStartedAtUtc,
            LastCompletedAtUtc = lastCompletedMetric?.LastCompletedAtUtc,
            TargetMetrics = targetMetrics
                .OrderByDescending(metric => metric.PendingSendCount)
                .ThenByDescending(metric => metric.MaxDurationMilliseconds)
                .ThenBy(metric => metric.TargetKind)
                .ThenBy(metric => metric.TargetCount)
                .ToList()
        };
    }
}

/// <summary>
/// Represents an active observed SignalR send operation.
/// </summary>
internal readonly struct SignalRSendMetricOperation(
    SignalRSendMetrics? owner,
    SignalRSendMetric? metric,
    SignalRSendMetricKey key,
    SignalRSendTarget? target,
    long startTimestamp)
{
    /// <summary>
    /// Gets a disabled operation that does not update metrics.
    /// </summary>
    public static SignalRSendMetricOperation Disabled { get; } = new(null, null, default, null, 0);

    /// <summary>
    /// Completes the operation.
    /// </summary>
    public void Complete(Exception? exception = null)
    {
        if (owner is null || metric is null || target is null)
        {
            return;
        }

        owner.CompleteObservedSend(metric, key, target, startTimestamp, exception);
    }
}
