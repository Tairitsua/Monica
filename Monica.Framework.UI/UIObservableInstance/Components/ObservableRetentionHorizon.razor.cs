using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Monica.Core.ObservableInstance.Models;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIObservableInstance.Components;

public partial class ObservableRetentionHorizon
{
    private static readonly DateTime ProcessStarted = ReadProcessStart();
    private static readonly string[] HorizonColumns = ["Instance", "State", "Window", "Buffer", "Exceptions"];
    private IReadOnlyDictionary<string, HorizonLane> _lanes = new Dictionary<string, HorizonLane>();
    private IReadOnlyList<DateTime> _ticks = [];
    private IReadOnlyList<ExceptionBurst> _bursts = [];
    private DateTime _start;
    private ObservableEvidence? _plotted;
    private bool _lastHour;
    private bool _byGroup;
    private bool _table;

    /// <summary>Gets the tracker captures and correlated retained bursts for the shared axis.</summary>
    [Parameter, EditorRequired] public ObservableEvidence Evidence { get; set; } = default!;
    /// <summary>Gets the tracker ID currently open in the evidence sheet.</summary>
    [Parameter] public string? SelectedId { get; set; }
    /// <summary>Opens a tracker from its label, a correlation flag, or the tabular view.</summary>
    [Parameter] public EventCallback<ObservableInspection> Inspect { get; set; }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_plotted, Evidence)) Replot();
    }

    private void ChangeWindow(bool lastHour)
    {
        if (_lastHour == lastHour) return;
        _lastHour = lastHour;
        Replot();
    }

    private void Replot()
    {
        _plotted = Evidence;
        var boot = Evidence.StartAt < ProcessStarted ? Evidence.StartAt : ProcessStarted;
        var baseline = Evidence.ReadAt.AddMinutes(-15);
        _start = _lastHour ? Evidence.ReadAt.AddHours(-1) : boot < baseline ? boot : baseline;

        var span = Evidence.ReadAt - _start;
        var step = span <= TimeSpan.FromMinutes(75) ? TimeSpan.FromMinutes(15) :
                   span <= TimeSpan.FromHours(2.5) ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(Math.Max(1, Math.Ceiling(span.TotalHours / 12)));
        var ticks = new List<DateTime>();
        var offset = Time.ToDisplayTime(new DateTimeOffset(_start)).Offset.Ticks;
        var first = _start.Ticks + step.Ticks - ((_start.Ticks + offset) % step.Ticks + step.Ticks) % step.Ticks;
        for (var tick = first; tick < Evidence.ReadAt.Ticks; tick += step.Ticks)
            ticks.Add(new DateTime(tick, DateTimeKind.Utc));
        _ticks = ticks;

        var bursts = new List<ExceptionBurst>();
        foreach (var burst in Evidence.Bursts.Where(burst => burst.End >= _start))
            if (bursts.All(prior => Math.Abs(Ratio(prior.Center) - Ratio(burst.Center)) >= 17))
                bursts.Add(burst);
        _bursts = bursts;
        _lanes = Evidence.Trackers.ToDictionary(item => item.Id, Project);
    }

    private HorizonLane Project(TrackerEvidence item)
    {
        var ribbons = new List<Ribbon>();
        var points = new List<HorizonPoint>();
        var retained = item.Snapshot.History;
        var evictedEnd = item.Oldest ?? Evidence.ReadAt;
        DateTime? evicted = item.EvictedRecords > 0 && Ratio(evictedEnd) > Ratio(item.Snapshot.RegisteredAt) ? evictedEnd : null;
        if (evicted is { } end && item.EvictedExceptions > 0)
        {
            var begin = item.Snapshot.RegisteredAt < _start ? _start : item.Snapshot.RegisteredAt;
            points.Add(new HorizonPoint(begin + (end - begin) / 2, null, true));
        }

        if (retained.Count > 0)
        {
            var cursor = retained[0].Timestamp;
            var role = TrackerEvidence.LevelRole(retained[0].LogLevel ?? item.Snapshot.CurrentLogLevel);
            foreach (var entry in retained)
            {
                var changed = entry.PreviousState != entry.CurrentState;
                if (changed || entry.LogLevel is LogLevel.Warning or LogLevel.Error or LogLevel.Critical)
                {
                    if (entry.Timestamp > cursor && entry.Timestamp > _start)
                        ribbons.Add(new Ribbon(cursor, entry.Timestamp, role));
                    cursor = entry.Timestamp;
                    role = TrackerEvidence.LevelRole(entry.LogLevel ?? item.Snapshot.CurrentLogLevel);
                }

                if (entry.Timestamp >= _start && (changed || entry.Exception is not null))
                    points.Add(new HorizonPoint(entry.Timestamp, entry));
            }
            ribbons.Add(new Ribbon(cursor, Evidence.ReadAt, TrackerEvidence.LevelRole(item.Snapshot.CurrentLogLevel)));
            if (points.Count == 0)
                points.Add(new HorizonPoint(retained[^1].Timestamp, retained[^1], Boundary: true));
        }
        else if (points.Count == 0)
        {
            points.Add(new HorizonPoint(Evidence.ReadAt, null));
        }

        return new HorizonLane(item, evicted, ribbons, points);
    }

    private string PointLabel(TrackerEvidence item, HorizonPoint point)
    {
        var detail = point switch
        {
            { Evicted: true } => L["Design:EvictedBadge", item.EvictedExceptions].Value,
            { Entry: { Exception: { } evidence } entry } => $"#{entry.Sequence} {ObservableText.ShortType(evidence.TypeName)} · {evidence.Message}",
            { Entry: { } entry } when entry.CurrentState != entry.PreviousState =>
                $"#{entry.Sequence} {ObservableText.Preview(entry.PreviousState.Text ?? "∅", 64)} → {ObservableText.Preview(entry.CurrentState.Text ?? "∅", 64)}",
            { Entry: { } entry } => $"#{entry.Sequence} {entry.Message}",
            _ => L["Design:NoHistoryLabel"].Value
        };
        return L["Design:HorizonReadout", item.Name, Time.FormatTime(point.At), detail];
    }

    private string BurstLabel(ExceptionBurst burst) => L["Design:HorizonBurstReadout", Time.FormatTime(burst.Start),
        burst.Trackers.Length, string.Join(", ", burst.Trackers.Select(item => item.Name))];

    private string Value(TrackerEvidence item) => item.Snapshot.CurrentState switch
    {
        { CaptureError: not null } => L["Design:ValueUnavailable"].Value,
        { Text: { Length: > 0 } text } => ObservableText.Preview(text, 28),
        _ => L["Design:NoState"].Value
    };

    private double Ratio(DateTime t) => Math.Clamp(100 * (t - _start).TotalSeconds / Math.Max(1, (Evidence.ReadAt - _start).TotalSeconds), 0, 100);
    private string X(DateTime t) => FormattableString.Invariant($"--oi-x:{Ratio(t):F4}%");
    private string Band(DateTime from, DateTime to) => FormattableString.Invariant($"left:{Ratio(from):F4}%;width:{Math.Max(0, Ratio(to) - Ratio(from)):F4}%");
    private static string On(bool value) => value ? "true" : "false";
    private static string Capital(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static DateTime ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    private sealed record HorizonLane(TrackerEvidence Item, DateTime? EvictedUntil, List<Ribbon> Ribbons, List<HorizonPoint> Points);
    private sealed record HorizonPoint(DateTime At, ObservableStateEntrySnapshot? Entry, bool Evicted = false, bool Boundary = false);
    private sealed record Ribbon(DateTime From, DateTime To, MoVisualRole Role);
}
