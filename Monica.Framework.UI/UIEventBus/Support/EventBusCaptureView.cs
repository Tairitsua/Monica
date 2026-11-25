using Monica.EventBus.Models;
using Monica.Framework.UI.UIEventBus.Models;

namespace Monica.Framework.UI.UIEventBus.Support;

internal enum EventBusCoverage
{
    Failed, Recovering, Subscribing, HealthyErr, Healthy, Stopped, Unreported, Local
}

internal sealed record EventBusTopicRow(
    string Topic, string? ServiceKey, TopicSubscriptionStatus? Status,
    EventBusProviderInfo? Provider, IReadOnlyList<SubscriptionViewModel> Handlers)
{
    internal string Id => EventBusCaptureView.TopicId(ServiceKey, Topic);
    internal EventBusCoverage Coverage => EventBusCaptureView.Coverage(Status, EventSubscriptionScope.Distributed);
    internal int Active => Handlers.Count(row => row.State == EventSubscriptionState.Active);
    internal bool IsOrphan => Handlers.Count == 0;
    internal bool IsActiveGap => Coverage == EventBusCoverage.Unreported && Active > 0;
    internal bool NeedsAttention => Coverage is EventBusCoverage.Failed or EventBusCoverage.Recovering || IsOrphan || IsActiveGap;
}

internal sealed record EventBusDistribution(string Key, string Title, int Count, int Active, int Risk);

/// <summary>Joins the independently sampled registration and transport planes of one workbench capture.</summary>
internal sealed class EventBusCaptureView
{
    internal static readonly EventBusCoverage[] MatrixColumns =
    [
        EventBusCoverage.Failed, EventBusCoverage.Recovering, EventBusCoverage.Subscribing,
        EventBusCoverage.HealthyErr, EventBusCoverage.Healthy, EventBusCoverage.Unreported, EventBusCoverage.Local
    ];

    internal static readonly string[] MatrixRows = ["Active", "Held", "PendingInactive", "Disabled", "Orphan"];
    internal static readonly EventSubscriptionState[] Registrations =
    [
        EventSubscriptionState.Active, EventSubscriptionState.Held, EventSubscriptionState.Pending,
        EventSubscriptionState.Inactive, EventSubscriptionState.Disabled, EventSubscriptionState.Disposed
    ];

    private readonly Dictionary<(EventSubscriptionScope Scope, string? Key), EventBusProviderInfo> _providers;
    private readonly Dictionary<string, EventBusTopicRow> _byId;
    private readonly Dictionary<(string Row, EventBusCoverage Column), int> _matrix;

    internal EventBusCaptureView(EventBusWorkbenchSnapshot snapshot)
    {
        Snapshot = snapshot;
        _providers = snapshot.Providers.GroupBy(provider => (provider.IsDistributed ? EventSubscriptionScope.Distributed :
            EventSubscriptionScope.Local, provider.ServiceKey)).ToDictionary(group => group.Key, group => group.First());

        var handlers = snapshot.Subscriptions.Where(row => row.Scope == EventSubscriptionScope.Distributed)
            .ToLookup(row => (row.ServiceKey, row.TopicName));
        var reported = snapshot.TopicStatuses.ToDictionary(status => (status.ServiceKey, status.TopicName));
        Topics = handlers.Select(group => group.Key).Union(reported.Keys).Select(key =>
            new EventBusTopicRow(key.TopicName, key.ServiceKey, reported.GetValueOrDefault(key),
                Provider(EventSubscriptionScope.Distributed, key.ServiceKey), handlers[key].ToArray()))
            .OrderBy(TopicPriority).ThenBy(topic => topic.Topic, StringComparer.Ordinal).ToArray();
        _byId = Topics.ToDictionary(topic => topic.Id, StringComparer.Ordinal);

        _matrix = snapshot.Subscriptions.Where(row => row.State != EventSubscriptionState.Disposed)
            .GroupBy(row => (MatrixRow(row.State), Coverage(row)))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var group in Topics.Where(topic => topic.IsOrphan).GroupBy(topic => topic.Coverage))
            _matrix[("Orphan", group.Key)] = group.Count();

        Attention = Topics.Count(topic => topic.NeedsAttention);
        Failed = Topics.Count(topic => topic.Coverage == EventBusCoverage.Failed);
        Recovering = Topics.Count(topic => topic.Coverage == EventBusCoverage.Recovering);
        ActiveGaps = Topics.Count(topic => topic.IsActiveGap);
        Orphans = Topics.Count(topic => topic.IsOrphan);
        Reported = snapshot.TopicStatuses.Count;
        Active = snapshot.Subscriptions.Count(row => row.State == EventSubscriptionState.Active);
        Distributed = snapshot.Subscriptions.Count(row => row.Scope == EventSubscriptionScope.Distributed);
        RetainedErrors = Topics.Count(topic => topic.Coverage == EventBusCoverage.HealthyErr);
        Shared = snapshot.Subscriptions.Count(IsShared);
        AtRisk = snapshot.Subscriptions.Count(row => row.IsUnhealthy);
        EventKinds = Rank(snapshot.Subscriptions.GroupBy(row => row.EventType), group => group.First().EventTypeShortName);
        TopicKinds = Rank(snapshot.Subscriptions.GroupBy(row => row.TopicName), group => group.Key);
    }

    internal EventBusWorkbenchSnapshot Snapshot { get; }
    internal IReadOnlyList<EventBusTopicRow> Topics { get; }
    internal IReadOnlyList<EventBusDistribution> EventKinds { get; }
    internal IReadOnlyList<EventBusDistribution> TopicKinds { get; }
    internal int Attention { get; }
    internal int Failed { get; }
    internal int Recovering { get; }
    internal int ActiveGaps { get; }
    internal int Orphans { get; }
    internal int Reported { get; }
    internal int Active { get; }
    internal int Distributed { get; }
    internal int RetainedErrors { get; }
    internal int Shared { get; }
    internal int AtRisk { get; }

    internal EventBusProviderInfo? Provider(EventSubscriptionScope scope, string? key) => _providers.GetValueOrDefault((scope, key));
    internal EventBusTopicRow? Topic(string id) => _byId.GetValueOrDefault(id);
    internal bool IsShared(SubscriptionViewModel row) => row.Scope == EventSubscriptionScope.Distributed
        && row.State is EventSubscriptionState.Held or EventSubscriptionState.Disabled
        && row.TopicRuntimeState == TopicSubscriptionRuntimeState.Healthy
        && Topic(TopicId(row.ServiceKey, row.TopicName)) is { Active: > 0 };

    internal int MatrixCount(string row, EventBusCoverage column) => _matrix.GetValueOrDefault((row, column));
    internal int MatrixTotal(string row) => MatrixColumns.Sum(column => MatrixCount(row, column));

    internal static EventBusCoverage Coverage(SubscriptionViewModel row) => Coverage(row.TopicStatus, row.Scope);

    internal static EventBusCoverage Coverage(TopicSubscriptionStatus? status, EventSubscriptionScope scope) =>
        scope == EventSubscriptionScope.Local ? EventBusCoverage.Local : status?.State switch
        {
            TopicSubscriptionRuntimeState.Failed => EventBusCoverage.Failed,
            TopicSubscriptionRuntimeState.Recovering => EventBusCoverage.Recovering,
            TopicSubscriptionRuntimeState.Subscribing => EventBusCoverage.Subscribing,
            TopicSubscriptionRuntimeState.Healthy when status.MessageErrorCount > 0 => EventBusCoverage.HealthyErr,
            TopicSubscriptionRuntimeState.Healthy => EventBusCoverage.Healthy,
            TopicSubscriptionRuntimeState.Stopped => EventBusCoverage.Stopped,
            _ => EventBusCoverage.Unreported
        };

    internal static string TopicId(string? key, string topic)
    {
        // Escaping preserves the key boundary even when a real key or topic contains "::".
        return key is null ? $"n::{topic}" : $"k:{Uri.EscapeDataString(key)}::{topic}";
    }

    internal static int Urgency(SubscriptionViewModel row) => (row.State, Coverage(row)) switch
    {
        (EventSubscriptionState.Active, EventBusCoverage.Failed) => 0,
        (EventSubscriptionState.Active, EventBusCoverage.Recovering) => 1,
        (EventSubscriptionState.Active, EventBusCoverage.Unreported) => 2,
        (EventSubscriptionState.Active, EventBusCoverage.HealthyErr) => 3,
        (EventSubscriptionState.Pending, _) => 4,
        (EventSubscriptionState.Held, _) => 5,
        (EventSubscriptionState.Active, _) => 6,
        (EventSubscriptionState.Inactive, _) => 7,
        (EventSubscriptionState.Disabled, _) => 8,
        _ => 9
    };

    private static int TopicPriority(EventBusTopicRow topic) => topic.Coverage switch
    {
        EventBusCoverage.Failed => 0,
        EventBusCoverage.Recovering => 1,
        EventBusCoverage.Unreported when topic.IsActiveGap => 2,
        _ when topic.IsOrphan => 3,
        EventBusCoverage.Subscribing => 4,
        EventBusCoverage.HealthyErr => 5,
        EventBusCoverage.Healthy => 6,
        EventBusCoverage.Stopped => 7,
        _ => 8
    };

    private static string MatrixRow(EventSubscriptionState state) => state is EventSubscriptionState.Pending or EventSubscriptionState.Inactive
        ? "PendingInactive" : state.ToString();

    private static EventBusDistribution[] Rank(IEnumerable<IGrouping<string, SubscriptionViewModel>> groups,
        Func<IGrouping<string, SubscriptionViewModel>, string> title) =>
        groups.Select(group => new EventBusDistribution(group.Key, title(group), group.Count(),
                group.Count(row => row.State == EventSubscriptionState.Active), group.Count(row => row.IsUnhealthy)))
            .OrderByDescending(item => item.Count).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray();
}
