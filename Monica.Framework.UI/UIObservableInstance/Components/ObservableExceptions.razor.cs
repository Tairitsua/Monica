using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIObservableInstance.Components;

public partial class ObservableExceptions
{
    private const int PageSize = 12;
    private static readonly string[] FilterKeys = ["exType", "source", "group", "level", "q", "from", "to"];
    private static readonly string[] MainLevels = ["Critical", "Error", "Warning", "Information"];
    private ExceptionEvidence[] _levelBase = [], _matches = [];
    private Bucket[] _buckets = [];
    private Lane[] _lanes = [];
    private string?[] _filterValues = [];
    private DateTime? _from, _to;
    private int _page;
    private bool _table;

    /// <summary>Gets the shared capture at the individual retained exception grain.</summary>
    [Parameter, EditorRequired] public ObservableEvidence Evidence { get; set; } = default!;
    /// <summary>Gets the route's exception filters and evidence selection.</summary>
    [Parameter, EditorRequired] public ObservableViewQuery Query { get; set; } = default!;
    /// <summary>Publishes filter changes, including both brushed time bounds, to the URL owner.</summary>
    [Parameter] public EventCallback<IReadOnlyDictionary<string, string?>> ChangeFilters { get; set; }
    /// <summary>Opens evidence at the sequence within its original registration.</summary>
    [Parameter] public EventCallback<ObservableInspection> Inspect { get; set; }

    private bool ByInstance => Query["lane"] == "instance";
    private string LaneKey => ByInstance ? "source" : "exType";
    private bool HasFilters => FilterKeys.Any(key => Query[key] is not null);
    private int Page => Math.Min(_page, Math.Max(0, (_matches.Length - 1) / PageSize));

    protected override void OnParametersSet()
    {
        var values = FilterKeys.Select(key => Query[key]).ToArray();
        if (!_filterValues.SequenceEqual(values)) _page = 0;
        _filterValues = values;
        _from = Query.Bound("from");
        _to = Query.Bound("to");
        _levelBase = Evidence.Exceptions.Where(MatchOtherFacets).ToArray();
        _matches = _levelBase.Where(item => Query["level"] is not { } level || item.Level == level).ToArray();
        Plot();
    }

    private bool MatchOtherFacets(ExceptionEvidence item)
    {
        if (Query["exType"] is { } type && item.Type != type ||
            Query["source"] is { } source && item.Tracker.Id != source ||
            Query["group"] is { } group && item.Tracker.GroupKey != group ||
            _from is { } from && item.Entry.Timestamp < from ||
            _to is { } to && item.Entry.Timestamp >= to)
            return false;

        return Query.Search.Length == 0 || ObservableText.Contains(Query.Search, item.Tracker.Id, item.Tracker.Name, item.Tracker.Group,
            item.Type, item.Entry.Message, item.Exception.Message, item.Exception.Details, $"#{item.Entry.Sequence}");
    }

    private IEnumerable<string> VisibleLevels() => MainLevels.Concat(Evidence.Exceptions.Select(item => item.Level)
        .Where(level => !MainLevels.Contains(level)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private void Plot()
    {
        _lanes = [];
        _buckets = [];
        if (_matches.Length == 0) return;

        var first = Evidence.StartAt;
        var last = Evidence.ReadAt;
        var start = _from is { } from && from > first ? from : first;
        var end = _to is { } to && to < last ? to : last;
        var span = (end - start).TotalMinutes;
        var minutes = span <= 20 ? 1 : span <= 90 ? 5 : span <= 360 ? 10 : Math.Max(30, 30 * Math.Ceiling(span / (30 * 48)));
        var step = TimeSpan.FromMinutes(minutes);
        // Align to the deployment clock at the first observation while preserving real elapsed time.
        var offset = Time.ToDisplayTime(new DateTimeOffset(start)).Offset.Ticks;
        var remainder = ((start.Ticks + offset) % step.Ticks + step.Ticks) % step.Ticks;
        var aligned = start.AddTicks(-remainder);
        var count = Math.Max(4, (int)Math.Ceiling((end - aligned) / step));
        _buckets = Enumerable.Range(0, count).Select(index =>
            new Bucket(aligned.AddTicks(index * step.Ticks), aligned.AddTicks((index + 1) * step.Ticks))).ToArray();

        var lanes = _matches.GroupBy(item => ByInstance ? item.Tracker.Id : item.Type).ToArray();
        foreach (var item in _matches)
        {
            var index = Math.Clamp((int)((item.Entry.Timestamp - aligned) / step), 0, count - 1);
            var bucket = _buckets[index];
            var key = ByInstance ? item.Tracker.Id : item.Type;
            bucket.Counts[key] = bucket.Counts.GetValueOrDefault(key) + 1;
            bucket.Total++;
        }

        _lanes = lanes.Select(group => new Lane(group.Key, ByInstance ? group.First().Tracker.Name : group.First().ShortType,
                group.Count(), _buckets.Max(bucket => bucket.Counts.GetValueOrDefault(group.Key))))
            .OrderByDescending(lane => lane.Total).ThenBy(lane => lane.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string BarStyle(int index, int count, int peak) => FormattableString.Invariant(
        $"--oi-slot:{100d / _buckets.Length:F4}%;--oi-center:{100d * (index + .5) / _buckets.Length:F4}%;height:{Math.Round(24d * count / peak)}px");

    private string BucketLabel(Bucket bucket) => L["Design:BucketReadout", Time.FormatTime(bucket.From), Time.FormatTime(bucket.To), bucket.Total];
    private string GroupName(TrackerEvidence item) => item.Group ?? L["Design:Ungrouped"].Value;
    private static string On(bool value) => value ? "true" : "false";

    private Task Change(string key, string? value) => ChangeFilters.InvokeAsync(new Dictionary<string, string?> { [key] = string.IsNullOrWhiteSpace(value) ? null : value });
    private Task Toggle(string key, string value) => Change(key, Query[key] == value ? null : value);
    private Task ResetWindow() => ChangeFilters.InvokeAsync(new Dictionary<string, string?> { ["from"] = null, ["to"] = null });
    private Task ClearAllAsync() => ChangeFilters.InvokeAsync(FilterKeys.ToDictionary(key => key, _ => (string?)null));

    private RenderFragment Finding() => builder =>
    {
        void Add(LocalizedString message, params RenderFragment[] values)
        {
            builder.AddContent(0, MoRichFinding.Compose(message.Value, values));
            builder.AddContent(1, " ");
        }

        if (_matches.Length == 0)
        {
            if (Evidence.Exceptions.Length > 0) Add(L["Design:ExLedeFiltered"], ObservableMarkup.Mark(Evidence.Exceptions.Length, MoVisualRole.Error));
            else if (Evidence.EvictedExceptions > 0)
                Add(L["Design:ExLedeEvictedOnly"], ObservableMarkup.Mark(Evidence.EvictedExceptions, MoVisualRole.Warning),
                    ObservableMarkup.Mark(Evidence.Capture.LifetimeExceptions, MoVisualRole.Warning));
            else Add(L["Design:ExLedeCalm"]);
            return;
        }

        Add(L["Design:ExLedeShown"], ObservableMarkup.Mark(_matches.Length, MoVisualRole.Error),
            ObservableMarkup.Mark(_matches.DistinctBy(item => item.Tracker.Id).Count(), MoVisualRole.Neutral));
        var type = _matches.GroupBy(item => item.Type).MaxBy(group => group.Count())!;
        Add(L["Design:ExLedeType"], ObservableMarkup.Mark(type.First().ShortType, MoVisualRole.Error,
            EventCallback.Factory.Create(this, () => Toggle("exType", type.Key))), ObservableMarkup.Mark(type.Count(), MoVisualRole.Error));
        if (Evidence.Bursts.FirstOrDefault(burst => burst.Entries.Count(_matches.Contains) >= 2) is { } burst)
            Add(L["Design:ExLedeBurst"], ObservableMarkup.Mark(Time.FormatTime(burst.Start), MoVisualRole.Error), ObservableMarkup.Mark(burst.Entries.Length, MoVisualRole.Error));
        if (Evidence.Evicted.FirstOrDefault() is { } evicted)
            Add(L["Design:ExLedeEvicted"], ObservableMarkup.Mark(evicted.Name, MoVisualRole.Warning,
                EventCallback.Factory.Create(this, () => Inspect.InvokeAsync(new(evicted.Id, "exceptions")))),
                ObservableMarkup.Mark(evicted.EvictedExceptions, MoVisualRole.Warning));
    };

    private sealed record Lane(string Key, string Label, int Total, int Peak);
    private sealed record Bucket(DateTime From, DateTime To)
    {
        internal int Total { get; set; }
        internal Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    }
}
