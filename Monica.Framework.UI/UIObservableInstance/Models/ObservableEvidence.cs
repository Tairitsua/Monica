using Microsoft.Extensions.Logging;
using Monica.Core.ObservableInstance.Models;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIObservableInstance.Models;

/// <summary>Projects one detached registry capture into tracker and exception evidence for the workbench.</summary>
public sealed class ObservableEvidence
{
    internal ObservableEvidence(ObservableInstanceCapture capture)
    {
        Capture = capture;
        Trackers = capture.Instances.Select(instance => new TrackerEvidence(instance))
            .OrderBy(item => item.Priority).ThenByDescending(item => item.RetainedExceptions.Length)
            .ThenByDescending(item => item.EvictedExceptions).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ById = Trackers.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Groups = Trackers.GroupBy(item => item.GroupKey).OrderByDescending(group => group.Count())
            .ThenBy(group => group.First().Group ?? "\uffff", StringComparer.OrdinalIgnoreCase).ToArray();
        Types = Trackers.GroupBy(item => item.TypeKey).OrderByDescending(group => group.Count())
            .ThenBy(group => group.First().Snapshot.InstanceTypeShortName ?? "\uffff", StringComparer.OrdinalIgnoreCase).ToArray();
        Exceptions = Trackers.SelectMany(item => item.RetainedExceptions.Select(entry => new ExceptionEvidence(item, entry)))
            .OrderByDescending(item => item.Entry.Timestamp).ThenBy(item => item.Tracker.Id, StringComparer.Ordinal)
            .ThenByDescending(item => item.Entry.Sequence).ToArray();
        ExceptionTypes = Exceptions.GroupBy(item => item.Type).OrderByDescending(group => group.Count())
            .ThenBy(group => group.First().ShortType, StringComparer.OrdinalIgnoreCase).ToArray();
        Bursts = FindBursts(Exceptions);
        Unhealthy = Trackers.Where(item => item.Health == HealthState.Unhealthy).ToArray();
        Recovered = Trackers.Where(item => item.Standing == "recovered").ToArray();
        Evicted = Trackers.Where(item => item.EvictedExceptions > 0).ToArray();
        ReviewCount = Trackers.Count(item => item.NeedsReview);
        SourceCount = Trackers.Count(item => item.RetainedExceptions.Length > 0);
    }

    internal ObservableInstanceCapture Capture { get; }
    internal DateTime ReadAt => Capture.ReadAt;
    internal DateTime StartAt => Trackers.Length == 0 ? ReadAt : Trackers.Min(item => item.Snapshot.RegisteredAt);
    internal TrackerEvidence[] Trackers { get; }
    internal IReadOnlyDictionary<string, TrackerEvidence> ById { get; }
    internal IGrouping<string, TrackerEvidence>[] Groups { get; }
    internal IGrouping<string, TrackerEvidence>[] Types { get; }
    internal ExceptionEvidence[] Exceptions { get; }
    internal IGrouping<string, ExceptionEvidence>[] ExceptionTypes { get; }
    internal TrackerEvidence[] Unhealthy { get; }
    internal TrackerEvidence[] Recovered { get; }
    internal TrackerEvidence[] Evicted { get; }
    internal int ReviewCount { get; }
    internal int SourceCount { get; }
    internal long EvictedExceptions => Capture.LifetimeExceptions - Exceptions.Length;
    internal long EvictedRecords => Capture.LifetimeRecords - Capture.RetainedRecords;
    internal IReadOnlyList<ExceptionBurst> Bursts { get; }

    private static ExceptionBurst[] FindBursts(ExceptionEvidence[] exceptions)
    {
        var bursts = new List<ExceptionBurst>();
        var ordered = exceptions.OrderBy(item => item.Entry.Timestamp).ToArray();
        for (var start = 0; start < ordered.Length;)
        {
            var end = start + 1;
            while (end < ordered.Length && ordered[end].Entry.Timestamp - ordered[start].Entry.Timestamp <= TimeSpan.FromSeconds(30))
                end++;

            var entries = ordered[start..end];
            var trackers = entries.Select(item => item.Tracker).DistinctBy(item => item.Id).ToArray();
            if (trackers.Length >= 2)
            {
                bursts.Add(new ExceptionBurst(entries, trackers));
                start = end;
            }
            else
            {
                // A late record from this tracker may still correlate with another registration.
                start++;
            }
        }

        return bursts.OrderByDescending(burst => burst.Trackers.Length).ThenByDescending(burst => burst.Entries.Length)
            .ThenByDescending(burst => burst.End).ToArray();
    }
}

internal sealed class TrackerEvidence(ObservableInstanceSnapshot snapshot)
{
    internal ObservableInstanceSnapshot Snapshot { get; } = snapshot;
    internal string Id => Snapshot.Identity.InstanceId;
    internal string Name => Snapshot.InstanceName;
    internal string? Group => string.IsNullOrEmpty(Snapshot.GroupId) ? null : Snapshot.GroupId;
    internal string GroupKey => Group is { } group ? $"group:{group}" : "ungrouped";
    internal string TypeKey => Snapshot.InstanceTypeName is { } type ? $"type:{type}" : "untyped";
    internal HealthState Health { get; } = ObservableInstanceCapture.Health(snapshot);
    internal ObservableStateEntrySnapshot[] RetainedExceptions { get; } = snapshot.History.Where(entry => entry.Exception is not null).ToArray();
    internal int Retained => Snapshot.History.Count;
    internal int EvictedRecords => Math.Max(0, Snapshot.TotalRecords - Retained);
    internal int EvictedExceptions => Math.Max(0, Snapshot.TotalExceptions - RetainedExceptions.Length);
    internal bool AtCapacity => Retained >= Snapshot.MaxHistorySize;
    internal bool NeedsReview => Health == HealthState.Unhealthy || RetainedExceptions.Length > 0 || EvictedExceptions > 0;
    internal DateTime? Oldest => Retained == 0 ? null : Snapshot.History[0].Timestamp;
    internal DateTime? Latest => Retained == 0 ? null : Snapshot.History[^1].Timestamp;
    internal long? LatestException => RetainedExceptions.LastOrDefault()?.Sequence;
    internal string Level => Snapshot.CurrentLogLevel?.ToString() ?? "Unknown";
    internal MoVisualRole HealthRole => Health switch
    {
        HealthState.Unhealthy => Snapshot.CurrentLogLevel == LogLevel.Warning ? MoVisualRole.Warning : MoVisualRole.Error,
        HealthState.Healthy => MoVisualRole.Success,
        _ => MoVisualRole.Neutral
    };

    internal int Priority => Snapshot.CurrentLogLevel switch
    {
        LogLevel.Critical => 0,
        LogLevel.Error => 1,
        LogLevel.Warning => 2,
        _ when RetainedExceptions.Length > 0 => 3,
        _ when EvictedExceptions > 0 => 4,
        _ when Health == HealthState.Unknown => 5,
        _ => 6
    };

    internal string Standing => Priority switch
    {
        0 => "critical",
        1 => "error",
        2 => "warning",
        3 => "recovered",
        4 => "evicted",
        5 => "unknown",
        _ => "healthy"
    };

    internal MoVisualRole StandingRole => Priority switch
    {
        0 or 1 or 3 => MoVisualRole.Error,
        2 or 4 => MoVisualRole.Warning,
        5 => MoVisualRole.Neutral,
        _ => MoVisualRole.Success
    };

    internal bool MatchesText(string search) => ObservableText.Contains(search, Id, Name, Snapshot.InstanceTypeName,
        Snapshot.InstanceKey, Group, Snapshot.CurrentState.Text);

    internal static MoVisualRole LevelRole(LogLevel? level) => level switch
    {
        LogLevel.Critical or LogLevel.Error => MoVisualRole.Error,
        LogLevel.Warning => MoVisualRole.Warning,
        LogLevel.Information => MoVisualRole.Info,
        LogLevel.Debug or LogLevel.Trace => MoVisualRole.Secondary,
        _ => MoVisualRole.Neutral
    };
}

internal sealed record ExceptionEvidence(TrackerEvidence Tracker, ObservableStateEntrySnapshot Entry)
{
    internal ObservableExceptionSnapshot Exception => Entry.Exception!;
    internal string Type => Exception.TypeName;
    internal string ShortType => ObservableText.ShortType(Type);
    internal string Level => Entry.LogLevel?.ToString() ?? "Unknown";
}

internal sealed record ExceptionBurst(ExceptionEvidence[] Entries, TrackerEvidence[] Trackers)
{
    internal DateTime Start => Entries[0].Entry.Timestamp;
    internal DateTime End => Entries[^1].Entry.Timestamp;
    internal DateTime Center => Start + (End - Start) / 2;
}

internal static class ObservableText
{
    internal static string ShortType(string type)
    {
        // A dot in a generic argument is not the enclosing type's namespace delimiter.
        var argument = type.IndexOf('<');
        var end = argument < 0 ? type.Length : argument;
        var dot = end == 0 ? -1 : type.LastIndexOf('.', end - 1);
        return type[(dot + 1)..];
    }

    internal static bool Contains(string search, params string?[] fields) =>
        fields.Any(field => field?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);

    internal static string Preview(string text, int limit)
    {
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        var length = end < 0 ? text.Length : end;
        return text[..Math.Min(length, limit)];
    }
}
