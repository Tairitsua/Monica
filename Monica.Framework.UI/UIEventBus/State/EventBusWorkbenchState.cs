using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.EventBus.Models;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.Support;

namespace Monica.Framework.UI.UIEventBus.State;

/// <summary>
/// Holds detached EventBus captures and bounded observed changes for one circuit without owning component lifetimes.
/// </summary>
/// <remarks>
/// Register this state as scoped. Components borrow it and own their cancellation sources, feeds, reader tasks,
/// refresh scheduling, dialogs, and observation generations. They must not dispose this state. It creates no timers,
/// component callbacks, or background tasks. Read <see cref="Snapshot"/> once when comparing its aggregates.
/// </remarks>
public sealed class EventBusWorkbenchState(
    EventBusMonitorService monitorService,
    EventBusProviderDiscoveryService providerDiscoveryService,
    ILogger<EventBusWorkbenchState> logger)
{
    private const int OBSERVED_CHANGE_LIMIT = 100;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();
    private EventBusWorkbenchSnapshot? _snapshot;
    private string? _refreshError;
    private bool _isRefreshing;
    private bool _hasAttemptedCapture;
    private ImmutableArray<SubscriptionChangeViewModel> _observedChanges = [];
    private long _observedChangeCount;
    private long _droppedChangeCount;
    private long _nextObservationGeneration;
    private long? _activeObservationGeneration;
    private long _currentFeedDroppedCount;

    /// <summary>Gets the last complete successful capture, or null before one has succeeded.</summary>
    public EventBusWorkbenchSnapshot? Snapshot
    {
        get { lock (_stateLock) { return _snapshot; } }
    }

    /// <summary>Gets the current capture's read-only handler view models, or an empty list before a capture.</summary>
    public IReadOnlyList<SubscriptionViewModel> Subscriptions => Snapshot?.Subscriptions ?? [];

    /// <summary>Gets the current capture's read-only provider view models, or an empty list before a capture.</summary>
    public IReadOnlyList<EventBusProviderInfo> Providers => Snapshot?.Providers ?? [];

    /// <summary>Gets the current capture's aggregates, or null before a capture.</summary>
    public SubscriptionStatistics? Statistics => Snapshot?.Statistics;

    /// <summary>Gets the current capture's adoption time without advancing it after a failed or cancelled refresh.</summary>
    public DateTimeOffset? CapturedAt => Snapshot?.CapturedAt;

    /// <summary>Gets the most recent refresh failure, retained until another refresh succeeds.</summary>
    public string? RefreshError
    {
        get { lock (_stateLock) { return _refreshError; } }
    }

    /// <summary>Gets whether an admitted refresh is currently reading or preparing a capture.</summary>
    public bool IsRefreshing
    {
        get { lock (_stateLock) { return _isRefreshing; } }
    }

    /// <summary>Gets whether the state is refreshing before its first successful capture.</summary>
    public bool IsInitialLoading
    {
        get { lock (_stateLock) { return _isRefreshing && _snapshot is null; } }
    }

    /// <summary>Gets whether a refresh has been admitted, including a failed or subsequently cancelled attempt.</summary>
    public bool HasAttemptedCapture
    {
        get { lock (_stateLock) { return _hasAttemptedCapture; } }
    }

    /// <summary>Gets at most 100 detached registry changes, newest first, observed by this circuit's mounted views.</summary>
    public IReadOnlyList<SubscriptionChangeViewModel> ObservedChanges
    {
        get { lock (_stateLock) { return _observedChanges; } }
    }

    /// <summary>Gets the number of accepted registry changes, including entries outside the retained 100-entry window.</summary>
    public long ObservedChangeCount
    {
        get { lock (_stateLock) { return _observedChangeCount; } }
    }

    /// <summary>
    /// Gets cumulative notification loss reported by accepted observation generations, independently of window truncation.
    /// This is observed feed-loss evidence, not a count of all host changes missed while no view was mounted.
    /// </summary>
    public long DroppedChangeCount
    {
        get { lock (_stateLock) { return _droppedChangeCount; } }
    }

    /// <summary>
    /// Serializes a refresh and atomically adopts subscriptions, providers, topic snapshots, and coherent derived counts.
    /// </summary>
    /// <param name="cancellationToken">Cancels admission and rejects any data or error result produced after cancellation.</param>
    /// <returns>An operation that finishes after capture adoption, failure recording, or cancellation.</returns>
    /// <remarks>
    /// A service failure retains the previous successful capture and records <see cref="RefreshError"/>. Cancellation
    /// propagates and retains both the previous capture and previous error. Existing synchronous service reads cannot
    /// be interrupted mid-call; cancellation is checked after each read and again at the adoption boundary.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The supplied token is cancelled.</exception>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                _isRefreshing = true;
                _hasAttemptedCapture = true;
            }

            var subscriptionResult = await monitorService.GetAllSubscriptionsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (subscriptionResult.IsFailed(out var subscriptionError, out var subscriptions))
            {
                RecordRefreshFailure(subscriptionError, cancellationToken);
                return;
            }

            var providerResult = providerDiscoveryService.GetRegisteredProviders();
            cancellationToken.ThrowIfCancellationRequested();
            if (providerResult.IsFailed(out var providerError, out var providers))
            {
                RecordRefreshFailure(providerError, cancellationToken);
                return;
            }

            var topicResult = monitorService.GetAllTopicStatuses();
            cancellationToken.ThrowIfCancellationRequested();
            if (topicResult.IsFailed(out var topicError, out var topicStatuses))
            {
                RecordRefreshFailure(topicError, cancellationToken);
                return;
            }

            var capturedTopics = (topicStatuses ?? []).Select(CloneTopicStatus).ToImmutableArray();
            var topicLookup = capturedTopics.ToDictionary(status => (status.ServiceKey, status.TopicName));
            var capturedSubscriptions = (subscriptions ?? []).Select(subscription =>
            {
                var captured = CloneSubscription(subscription);
                captured.TopicStatus = captured.Scope == EventSubscriptionScope.Distributed
                    && topicLookup.TryGetValue((captured.ServiceKey, captured.TopicName), out var topicStatus)
                        ? topicStatus
                        : null;
                return captured;
            }).ToImmutableArray();
            var capturedProviders = (providers ?? []).Select(provider => CloneProvider(
                provider, capturedSubscriptions, capturedTopics)).ToImmutableArray();
            var statistics = CreateStatistics(capturedSubscriptions, capturedTopics);
            var capture = new EventBusWorkbenchSnapshot(
                DateTimeOffset.UtcNow, capturedSubscriptions, capturedProviders, capturedTopics, statistics);

            lock (_stateLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _snapshot = capture;
                _refreshError = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_stateLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _refreshError = exception.GetMessageRecursively();
            }

            logger.LogError(exception, "Failed to capture EventBus workbench diagnostics");
        }
        finally
        {
            lock (_stateLock)
            {
                _isRefreshing = false;
            }

            _refreshGate.Release();
        }
    }

    /// <summary>Applies the shared monitor filter to this state's current captured subscriptions.</summary>
    /// <param name="filter">Conjunctive registration, runtime, provider, discovery, scope, and search filters.</param>
    /// <returns>Read-only-by-contract captured rows in descending creation order.</returns>
    public List<SubscriptionViewModel> ApplyFilter(SubscriptionFilter filter) =>
        EventBusMonitorService.ApplyFilter(Subscriptions, filter);

    /// <summary>Admits one mounted view as the current owner of observation updates.</summary>
    /// <returns>A generation that the component passes with every notification and retires when it unmounts.</returns>
    /// <remarks>
    /// Starting a newer generation rejects updates from any previous mounted view even if its asynchronous reader has
    /// not finished retiring. Captured data and the retained observed-change window survive route changes.
    /// </remarks>
    public long BeginObservation()
    {
        lock (_stateLock)
        {
            _activeObservationGeneration = ++_nextObservationGeneration;
            _currentFeedDroppedCount = 0;
            return _activeObservationGeneration.Value;
        }
    }

    /// <summary>Retires a mounted view's generation without allowing it to retire a newer owner.</summary>
    /// <param name="generation">The exact generation returned when this view began observing.</param>
    public void EndObservation(long generation)
    {
        lock (_stateLock)
        {
            if (_activeObservationGeneration == generation)
            {
                _activeObservationGeneration = null;
            }
        }
    }

    /// <summary>Records detached observed registry changes and feed-loss evidence from the current mounted view.</summary>
    /// <param name="generation">The mounted view's observation generation.</param>
    /// <param name="change">A read-only notification from the component-owned feed.</param>
    /// <param name="droppedChangeCount">That feed's current cumulative notification-loss count.</param>
    /// <param name="cancellationToken">The mounted view's lifetime token; cancellation rejects late notifications.</param>
    /// <returns>True when the generation and lifetime still permit adoption; false for a retired or cancelled owner.</returns>
    /// <remarks>
    /// Topic notifications contribute loss evidence but do not become subscription-lifecycle history entries.
    /// Notifications never mutate <see cref="Snapshot"/>; the component schedules a separate refresh.
    /// </remarks>
    public bool RecordChange(
        long generation,
        EventBusMonitorChange change,
        long droppedChangeCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentOutOfRangeException.ThrowIfNegative(droppedChangeCount);
        lock (_stateLock)
        {
            if (_activeObservationGeneration != generation || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (droppedChangeCount > _currentFeedDroppedCount)
            {
                _droppedChangeCount += droppedChangeCount - _currentFeedDroppedCount;
                _currentFeedDroppedCount = droppedChangeCount;
            }

            if (change is EventBusMonitorChange.SubscriptionChanged registryChange)
            {
                var detached = new SubscriptionChangeViewModel
                {
                    ChangeType = registryChange.Change.ChangeType,
                    Timestamp = registryChange.Change.Timestamp,
                    Subscription = CloneSubscription(registryChange.Change.Subscription)
                };
                _observedChanges = _observedChanges.Insert(0, detached);
                if (_observedChanges.Length > OBSERVED_CHANGE_LIMIT)
                {
                    _observedChanges = _observedChanges.RemoveAt(OBSERVED_CHANGE_LIMIT);
                }

                _observedChangeCount++;
            }

            return true;
        }
    }

    /// <summary>Copies a captured or notification row for an independently frozen inspector or editable consumer.</summary>
    /// <param name="subscription">The source row, which is treated as read-only.</param>
    /// <returns>A new row with detached metadata and an immutable copy of topic error history.</returns>
    public static SubscriptionViewModel CloneSubscription(SubscriptionViewModel subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new SubscriptionViewModel
        {
            SubscriptionId = subscription.SubscriptionId,
            EventType = subscription.EventType,
            EventTypeShortName = subscription.EventTypeShortName,
            TopicName = subscription.TopicName,
            ServiceKey = subscription.ServiceKey,
            HandlerType = subscription.HandlerType,
            HandlerTypeShortName = subscription.HandlerTypeShortName,
            HandlerFactoryType = subscription.HandlerFactoryType,
            ActionMethodName = subscription.ActionMethodName,
            ActionDeclaringType = subscription.ActionDeclaringType,
            ActionMethodSignature = subscription.ActionMethodSignature,
            ActionIsStatic = subscription.ActionIsStatic,
            Scope = subscription.Scope,
            State = subscription.State,
            Activation = subscription.Activation,
            TopicStatus = subscription.TopicStatus is { } status ? CloneTopicStatus(status) : null,
            CreatedAt = subscription.CreatedAt,
            ActivatedAt = subscription.ActivatedAt,
            DeactivatedAt = subscription.DeactivatedAt,
            IsAutoDiscovered = subscription.IsAutoDiscovered,
            Metadata = new Dictionary<string, string>(subscription.Metadata, StringComparer.Ordinal)
        };
    }

    private static TopicSubscriptionStatus CloneTopicStatus(TopicSubscriptionStatus status) =>
        status with { RecentErrors = status.RecentErrors.ToImmutableArray() };

    private void RecordRefreshFailure(Res error, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _refreshError = string.IsNullOrWhiteSpace(error.Message) ? error.Status.ToString() : error.Message;
        }
    }

    private static EventBusProviderInfo CloneProvider(
        EventBusProviderInfo provider,
        IReadOnlyList<SubscriptionViewModel> subscriptions,
        IReadOnlyList<TopicSubscriptionStatus> topicStatuses)
    {
        var scope = provider.IsDistributed ? EventSubscriptionScope.Distributed : EventSubscriptionScope.Local;
        var matches = subscriptions.Where(subscription => subscription.Scope == scope
            && string.Equals(subscription.ServiceKey, provider.ServiceKey, StringComparison.Ordinal)).ToList();
        return new EventBusProviderInfo
        {
            ServiceKey = provider.ServiceKey,
            ProviderType = provider.ProviderType,
            Capabilities = provider.Capabilities,
            IsDistributed = provider.IsDistributed,
            ImplementationType = provider.ImplementationType,
            OptionDiagnosticsTarget = provider.OptionDiagnosticsTarget,
            SubscriptionCount = matches.Count,
            ActiveSubscriptionCount = matches.Count(subscription => subscription.State == EventSubscriptionState.Active),
            UnhealthyTopicCount = provider.IsDistributed
                ? topicStatuses.Count(status => string.Equals(status.ServiceKey, provider.ServiceKey, StringComparison.Ordinal)
                    && status.State is TopicSubscriptionRuntimeState.Recovering or TopicSubscriptionRuntimeState.Failed)
                : 0
        };
    }

    private static SubscriptionStatistics CreateStatistics(
        IReadOnlyList<SubscriptionViewModel> subscriptions,
        IReadOnlyList<TopicSubscriptionStatus> topicStatuses) => new()
        {
            TotalSubscriptions = subscriptions.Count,
            ActiveSubscriptions = subscriptions.Count(subscription => subscription.State == EventSubscriptionState.Active),
            InactiveSubscriptions = subscriptions.Count(subscription => subscription.State == EventSubscriptionState.Inactive),
            PendingSubscriptions = subscriptions.Count(subscription => subscription.State == EventSubscriptionState.Pending),
            DisposedSubscriptions = subscriptions.Count(subscription => subscription.State == EventSubscriptionState.Disposed),
            HeldSubscriptions = subscriptions.Count(subscription => subscription.State == EventSubscriptionState.Held),
            DisabledSubscriptions = subscriptions.Count(subscription => subscription.State == EventSubscriptionState.Disabled),
            UnhealthySubscriptions = subscriptions.Count(subscription => subscription.IsUnhealthy),
            UnhealthyTopics = topicStatuses.Count(status => status.State is TopicSubscriptionRuntimeState.Recovering or TopicSubscriptionRuntimeState.Failed),
            LocalSubscriptions = subscriptions.Count(subscription => subscription.Scope == EventSubscriptionScope.Local),
            DistributedSubscriptions = subscriptions.Count(subscription => subscription.Scope == EventSubscriptionScope.Distributed),
            AutoDiscoveredCount = subscriptions.Count(subscription => subscription.IsAutoDiscovered),
            ManualSubscriptionCount = subscriptions.Count(subscription => !subscription.IsAutoDiscovered),
            ActionHandlerCount = subscriptions.Count(subscription => subscription.IsActionHandler),
            TypeHandlerCount = subscriptions.Count(subscription => !subscription.IsActionHandler),
            TopEventTypes = subscriptions.GroupBy(subscription => subscription.EventType, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(10)
                .Select(group => new EventTypeCount
                {
                    EventType = group.Key,
                    EventTypeShortName = group.First().EventTypeShortName,
                    Count = group.Count()
                }).ToList(),
            TopTopics = subscriptions.GroupBy(subscription => subscription.TopicName, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(10)
                .Select(group => new TopicCount { TopicName = group.Key, Count = group.Count() }).ToList()
        };
}
