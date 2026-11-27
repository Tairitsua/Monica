using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using Monica.Core.ObservableInstance.Models;
using Monica.Framework.UI.UIObservableInstance.Models;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Framework.UI.UIObservableInstance.Components;

public partial class ObservableRegistry
{
    private const int PageSize = 15;
    private static readonly string[] FilterKeys = ["health", "group", "type", "level", "evidence", "q", "from", "to"];
    private static readonly string[] EvidenceOptions = ["retainedEx", "evictedEx", "full", "critErr"];
    private static readonly string[] SortOptions = ["attention", "lastRecorded", "retainedEx", "churn", "name"];
    private static readonly string[] LevelOptions = ["Critical", "Error", "Warning", "Information", "Debug", "Trace", "None", "Unknown"];
    private static readonly (string Name, MoVisualRole Role)[] HealthFacets =
        [("Unhealthy", MoVisualRole.Error), ("Healthy", MoVisualRole.Success), ("Unknown", MoVisualRole.Neutral)];
    private TrackerEvidence[] _matches = [], _healthBase = [];
    private DateTime? _from, _to;
    private int _page;
    private bool _matrix;
    private string?[] _filterValues = [];

    /// <summary>Gets the shared capture projection.</summary>
    [Parameter, EditorRequired] public ObservableEvidence Evidence { get; set; } = default!;
    /// <summary>Gets the route's current filters and inspector selection.</summary>
    [Parameter, EditorRequired] public ObservableViewQuery Query { get; set; } = default!;
    /// <summary>Publishes registry filter changes to the workspace URL owner.</summary>
    [Parameter] public EventCallback<IReadOnlyDictionary<string, string?>> ChangeFilters { get; set; }
    /// <summary>Opens the requested frozen tracker evidence.</summary>
    [Parameter] public EventCallback<ObservableInspection> Inspect { get; set; }
    /// <summary>Requests source-qualified history clearing after confirmation.</summary>
    [Parameter] public EventCallback<ObservableInstanceSnapshot> Clear { get; set; }
    /// <summary>Disables maintenance while a command is in progress.</summary>
    [Parameter] public bool Busy { get; set; }

    /// <summary>Gets the scope note that follows the registration inventory.</summary>
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private bool HasFilters => FilterKeys.Any(key => Query[key] is not null);
    private bool Grouped => Query["grouping"] == "group";
    private int Page => Math.Min(_page, Math.Max(0, (_matches.Length - 1) / PageSize));

    protected override void OnParametersSet()
    {
        var filterValues = FilterKeys.Append("sort").Append("grouping").Select(key => Query[key]).ToArray();
        if (!_filterValues.SequenceEqual(filterValues)) _page = 0;
        _filterValues = filterValues;
        _from = ReadWallTime("from");
        _to = ReadWallTime("to");
        _healthBase = Evidence.Trackers.Where(MatchesOtherFacets).ToArray();
        var filtered = _healthBase.Where(item => Query["health"] is not { } health || item.Health.ToString() == health);
        _matches = (Query["sort"] switch
        {
            "lastRecorded" => filtered.OrderByDescending(item => item.Snapshot.TotalRecords == 0 ? DateTime.MinValue : item.Snapshot.LastRecordedAt)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "retainedEx" => filtered.OrderByDescending(item => item.RetainedExceptions.Length).ThenByDescending(item => item.Snapshot.TotalExceptions)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "churn" => filtered.OrderByDescending(item => item.EvictedRecords).ThenByDescending(item => (double)item.Retained / item.Snapshot.MaxHistorySize)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "name" => filtered.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered
        }).ToArray();
    }

    private bool MatchesOtherFacets(TrackerEvidence item)
    {
        if (Query["group"] is { } group && group != item.GroupKey ||
            Query["type"] is { } type && type != item.TypeKey ||
            Query["level"] is { } level && level != item.Level) return false;

        var evidence = Query["evidence"] switch
        {
            "retainedEx" => item.RetainedExceptions.Length > 0,
            "evictedEx" => item.EvictedExceptions > 0,
            "full" => item.AtCapacity,
            "critErr" => item.Snapshot.CurrentLogLevel is LogLevel.Critical or LogLevel.Error,
            _ => true
        };
        if (!evidence) return false;

        var registered = Time.ToDisplayTime(item.Snapshot.RegisteredAt);
        return (_from is null || registered >= _from) && (_to is null || registered <= _to)
            && (Query.Search.Length == 0 || item.MatchesText(Query.Search));
    }

    private DateTime? ReadWallTime(string key) => DateTime.TryParseExact(Query[key], ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    private Task ChangeDate(string key, DateTime? value) => Change(key, value?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
    private Task Change(string key, string? value) => ChangeFilters.InvokeAsync(new Dictionary<string, string?> { [key] = string.IsNullOrWhiteSpace(value) ? null : value });
    private Task Toggle(string key, string value) => Change(key, Query[key] == value ? null : value);
    private Task ClearAllAsync() => ChangeFilters.InvokeAsync(FilterKeys.ToDictionary(key => key, _ => (string?)null));
    private Task Open(TrackerEvidence item, string? tab = null, long? seq = null) => Inspect.InvokeAsync(new ObservableInspection(item.Id, tab, seq));
    private string GroupLabel(TrackerEvidence item) => item.Group ?? L["Design:Ungrouped"].Value;
    private string MatrixLabel(TrackerEvidence item) => $"{item.Name} · {GroupLabel(item)} · {L[$"Design:Standings:{Capitalize(item.Standing)}"]}";
    private static string Pressed(bool value) => value ? "true" : "false";
    private static string Capitalize(string key) => char.ToUpperInvariant(key[0]) + key[1..];

    private IEnumerable<(string Key, int Count, MoVisualRole Role)> Reasons()
    {
        yield return ("Unhealthy", Evidence.Unhealthy.Length, MoVisualRole.Error);
        yield return ("Recovered", Evidence.Recovered.Length, MoVisualRole.Error);
        yield return ("Evicted", Evidence.Trackers.Count(item => item.Standing == "evicted"), MoVisualRole.Warning);
        yield return ("Unknown", Evidence.Capture.UnknownCount, MoVisualRole.Neutral);
    }

    private IEnumerable<(string Key, long Count, MoVisualRole Role, string Detail)> Facts()
    {
        var capture = Evidence.Capture;
        yield return ("RetainedRec", capture.RetainedRecords, Evidence.EvictedRecords > 0 ? MoVisualRole.Warning : MoVisualRole.Info,
            Evidence.EvictedRecords > 0 ? L["Design:FactRetainedRecords", Evidence.EvictedRecords.ToString("N0"), Evidence.Trackers.Count(item => item.AtCapacity)] : L["Design:FactRetainedRecordsCalm"]);
        yield return ("RetainedEx", Evidence.Exceptions.Length, Evidence.Exceptions.Length > 0 ? MoVisualRole.Error : MoVisualRole.Success,
            Evidence.Exceptions.Length > 0 ? L["Design:FactRetainedExceptions", Evidence.SourceCount] : L["Design:FactRetainedExceptionsCalm"]);
        yield return ("LifetimeRec", capture.LifetimeRecords, MoVisualRole.Neutral,
            L["Design:FactLifetimeRecords", Evidence.Trackers.Count(item => item.Snapshot.TotalRecords > 0)]);
        yield return ("LifetimeEx", capture.LifetimeExceptions, Evidence.EvictedExceptions > 0 ? MoVisualRole.Warning :
            capture.LifetimeExceptions > 0 ? MoVisualRole.Error : MoVisualRole.Neutral,
            Evidence.EvictedExceptions > 0 ? L["Design:FactLifetimeExceptions", Evidence.EvictedExceptions] : L["Design:FactLifetimeExceptionsCalm"]);
    }

    private RenderFragment Finding() => builder =>
    {
        void Add(LocalizedString message, params RenderFragment[] values)
        {
            builder.AddContent(0, MoRichFinding.Compose(message.Value, values));
            builder.AddContent(1, " ");
        }
        RenderFragment Link(TrackerEvidence item, MoVisualRole role) => ObservableMarkup.Mark(item.Name, role,
            EventCallback.Factory.Create(this, () => Open(item, item.LatestException is null ? null : "exceptions", item.LatestException)));

        if (Evidence.ReviewCount == 0)
            Add(L["Design:LedeCalm"], ObservableMarkup.Mark(Evidence.Trackers.Length, MoVisualRole.Success),
                ObservableMarkup.Mark(Evidence.Capture.RetainedRecords.ToString("N0"), MoVisualRole.Info));
        if (Evidence.Unhealthy.FirstOrDefault() is { } worst)
            Add(L["Design:LedeUnhealthy"], ObservableMarkup.Mark(Evidence.Unhealthy.Length, worst.StandingRole), Link(worst, worst.StandingRole),
                ObservableMarkup.Mark(L[$"Design:Levels:{worst.Level}"].Value, worst.StandingRole));
        if (Evidence.Bursts.FirstOrDefault() is { } burst)
            Add(L["Design:LedeBurst"], ObservableMarkup.Mark(Time.FormatTime(burst.Start), MoVisualRole.Error), ObservableMarkup.Mark(burst.Trackers.Length, MoVisualRole.Error));
        if (Evidence.Recovered.Length > 0)
            Add(L["Design:LedeRecovered"], ObservableMarkup.Mark(Evidence.Recovered.Length, MoVisualRole.Error),
                ObservableMarkup.Mark(Evidence.Recovered.Sum(item => item.RetainedExceptions.Length), MoVisualRole.Error));
        if (Evidence.Evicted.FirstOrDefault() is { } evicted)
            Add(L["Design:LedeEvicted"], Link(evicted, MoVisualRole.Warning), ObservableMarkup.Mark(evicted.EvictedExceptions, MoVisualRole.Warning));
        if (Evidence.Trackers.FirstOrDefault(item => item.Snapshot.CurrentState.CaptureError is not null) is { } failed)
            Add(L["Design:LedeCaptureError"], Link(failed, MoVisualRole.Warning));
    };
}
