using System.Threading.Channels;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Constants;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Tool.Extensions;

namespace Monica.Framework.UI.UIEventBus.State;

/// <summary>
/// Queries this host's EventBus subscriptions and owns broadcast change feeds for monitor consumers.
/// </summary>
/// <remarks>
/// The host owns this singleton. Components own and retire their individual change feeds; they must not dispose
/// the service. Notifications are transient invalidation hints rather than a durable event or delivery log.
/// </remarks>
public sealed class EventBusMonitorService(
    IEventSubscriptionRegistry subscriptionManager,
    ITopicSubscriptionStatusStore topicStatusStore,
    IStringLocalizer<EventBusResource> localizer,
    ILogger<EventBusMonitorService> logger) : IAsyncDisposable
{
    private readonly IEventSubscriptionRegistry _subscriptionManager = subscriptionManager;
    private readonly ITopicSubscriptionStatusStore _topicStatusStore = topicStatusStore;

    private readonly object _lifetimeLock = new();
    private readonly Dictionary<Guid, EventBusMonitorChangeFeed> _changeFeeds = new();
    private IDisposable? _registryObserver;
    private bool _initialized;
    private bool _disposed;

    #region Initialization

    /// <summary>
    /// Initializes source observation while the lifetime lock is held.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            // All local and distributed buses share one subscription registry.
            _registryObserver = _subscriptionManager.Subscribe(
                new SubscriptionChangeObserver(change => {
                    if (EventBusTestMetadataKeys.IsTestListenerSubscription(change.Subscription))
                    {
                        return;
                    }

                    PublishChange(new EventBusMonitorChange.SubscriptionChanged(MapToChangeViewModel(change)));
                    logger.LogDebug("EventBus subscription change: {ChangeType} - {EventType}",
                        change.ChangeType, change.Subscription.EventType.Name);
                }));

            _topicStatusStore.StatusChanged += OnTopicStatusChanged;

            _initialized = true;
            logger.LogInformation("EventBusMonitorService initialized successfully");
        }
        catch (Exception ex)
        {
            _registryObserver?.Dispose();
            _registryObserver = null;
            logger.LogError(ex, "Failed to initialize EventBusMonitorService");
            throw;
        }
    }

    private void OnTopicStatusChanged(TopicSubscriptionStatus status)
    {
        PublishChange(new EventBusMonitorChange.TopicStatusChanged(status));
    }

    private void PublishChange(EventBusMonitorChange change)
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var feed in _changeFeeds.Values)
            {
                feed.TryPublish(change);
            }
        }
    }

    private void RetireFeed(Guid id)
    {
        lock (_lifetimeLock)
        {
            _changeFeeds.Remove(id);
        }
    }

    /// <summary>
    /// Observable observer implementation
    /// </summary>
    private class SubscriptionChangeObserver(Action<EventSubscriptionChange> onNext) : IObserver<EventSubscriptionChange>
    {
        public void OnNext(EventSubscriptionChange value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    #endregion

    #region Query Methods

    /// <summary>
    /// Get all subscriptions
    /// </summary>
    public async Task<Res<List<SubscriptionViewModel>>> GetAllSubscriptionsAsync()
    {
        try
        {
            var allSubscriptions = GetVisibleSubscriptions();

            var allSubs = allSubscriptions
                .Select(MapToViewModel)
                .OrderByDescending(s => s.CreatedAt)
                .ToList();

            var localCount = allSubscriptions.Count(s => s.Scope == EventSubscriptionScope.Local);
            var distCount = allSubscriptions.Count(s => s.Scope == EventSubscriptionScope.Distributed);

            logger.LogDebug("Retrieved {Count} subscriptions (Local: {LocalCount}, Distributed: {DistCount})",
                allSubs.Count, localCount, distCount);

            return Res.Ok(allSubs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get all subscriptions");
            return Res.Fail(localizer["Services:Monitor:GetSubscriptionsFailed", ex.GetMessageRecursively()], ResStatus.InternalError);
        }
    }

    /// <summary>
    /// Get subscription based on ID
    /// </summary>
    public async Task<Res<SubscriptionViewModel?>> GetSubscriptionByIdAsync(EventSubscriptionId subscriptionId)
    {
        try
        {
            var subscription = _subscriptionManager.GetById(subscriptionId);

            if (subscription == null)
            {
                return Res.Ok<SubscriptionViewModel?>(null);
            }

            var vm = MapToViewModel(subscription);
            return Res.Ok<SubscriptionViewModel?>(vm);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get subscription by ID: {EventSubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Monitor:GetSubscriptionDetailFailed", ex.GetMessageRecursively()], ResStatus.InternalError);
        }
    }

    /// <summary>
    /// Queries visible subscriptions using the shared monitor filter contract.
    /// </summary>
    /// <param name="filter">Conjunctive registration, runtime, scope, provider, discovery, and text filters.</param>
    /// <returns>Matching snapshots ordered by descending creation time, or a query failure.</returns>
    public async Task<Res<List<SubscriptionViewModel>>> FilterSubscriptionsAsync(SubscriptionFilter filter)
    {
        try
        {
            var result = ApplyFilter(GetVisibleSubscriptions().Select(MapToViewModel), filter);

            logger.LogDebug("Filtered subscriptions: {Count} results", result.Count);
            return Res.Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to filter subscriptions");
            return Res.Fail(localizer["Services:Monitor:FilterSubscriptionsFailed", ex.GetMessageRecursively()], ResStatus.InternalError);
        }
    }

    /// <summary>
    /// Applies the same filter to already captured subscription snapshots without querying the registry again.
    /// </summary>
    /// <param name="subscriptions">Captured subscription snapshots to filter.</param>
    /// <param name="filter">
    /// Filters combine conjunctively. A non-null service key matches ordinally, including empty or whitespace keys;
    /// null leaves that filter unset. Selecting a provider matches its exact nullable key and bus scope.
    /// Registration and topic runtime states remain independent; an unreported runtime state matches no concrete state.
    /// </param>
    /// <returns>Matching snapshots ordered by descending creation time.</returns>
    /// <remarks>
    /// Text search is case-insensitive over clean event and handler names, topic, service key, action metadata, and
    /// subscription ID. A complete GUID also matches the ID regardless of its hyphenated representation.
    /// </remarks>
    public static List<SubscriptionViewModel> ApplyFilter(
        IEnumerable<SubscriptionViewModel> subscriptions,
        SubscriptionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(filter);

        var searchText = filter.SearchText?.Trim();
        var hasSearch = !string.IsNullOrEmpty(searchText);
        var hasSearchId = Guid.TryParse(searchText, out var searchId);

        return subscriptions.Where(subscription =>
        {
            if (filter.State.HasValue && subscription.State != filter.State.Value
                || filter.TopicRuntimeState.HasValue && subscription.TopicRuntimeState != filter.TopicRuntimeState.Value
                || filter.Scope.HasValue && subscription.Scope != filter.Scope.Value
                || filter.IsAutoDiscovered.HasValue && subscription.IsAutoDiscovered != filter.IsAutoDiscovered.Value
                || filter.ServiceKey is not null
                    && !string.Equals(subscription.ServiceKey, filter.ServiceKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (filter.SelectedProvider is { } provider
                && (!string.Equals(subscription.ServiceKey, provider.ServiceKey, StringComparison.Ordinal)
                    || subscription.Scope != (provider.IsDistributed
                        ? EventSubscriptionScope.Distributed
                        : EventSubscriptionScope.Local)))
            {
                return false;
            }

            return !hasSearch
                || ContainsSearch(subscription.EventType, searchText!)
                || ContainsSearch(subscription.EventTypeShortName, searchText!)
                || ContainsSearch(subscription.TopicName, searchText!)
                || ContainsSearch(subscription.ServiceKey, searchText!)
                || ContainsSearch(subscription.HandlerType, searchText!)
                || ContainsSearch(subscription.HandlerTypeShortName, searchText!)
                || ContainsSearch(subscription.HandlerFactoryType, searchText!)
                || ContainsSearch(subscription.ActionMethodName, searchText!)
                || ContainsSearch(subscription.ActionDeclaringType, searchText!)
                || ContainsSearch(subscription.ActionMethodSignature, searchText!)
                || ContainsSearch(subscription.SubscriptionId, searchText!)
                || hasSearchId && Guid.TryParse(subscription.SubscriptionId, out var subscriptionId)
                    && subscriptionId == searchId;
        }).OrderByDescending(subscription => subscription.CreatedAt).ToList();
    }

    private static bool ContainsSearch(string? value, string searchText) =>
        value?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Get statistics
    /// </summary>
    public async Task<Res<SubscriptionStatistics>> GetStatisticsAsync()
    {
        try
        {
            var allSubs = GetVisibleSubscriptions();
            var viewModels = allSubs.Select(MapToViewModel).ToList();

            var stats = new SubscriptionStatistics
            {
                TotalSubscriptions = viewModels.Count,
                ActiveSubscriptions = viewModels.Count(s => s.State == EventSubscriptionState.Active),
                InactiveSubscriptions = viewModels.Count(s => s.State == EventSubscriptionState.Inactive),
                PendingSubscriptions = viewModels.Count(s => s.State == EventSubscriptionState.Pending),
                DisposedSubscriptions = viewModels.Count(s => s.State == EventSubscriptionState.Disposed),
                HeldSubscriptions = viewModels.Count(s => s.State == EventSubscriptionState.Held),
                DisabledSubscriptions = viewModels.Count(s => s.State == EventSubscriptionState.Disabled),
                UnhealthySubscriptions = viewModels.Count(s => s.IsUnhealthy),
                LocalSubscriptions = viewModels.Count(s => s.Scope == EventSubscriptionScope.Local),
                DistributedSubscriptions = viewModels.Count(s => s.Scope == EventSubscriptionScope.Distributed),
                AutoDiscoveredCount = viewModels.Count(s => s.IsAutoDiscovered),
                ManualSubscriptionCount = viewModels.Count(s => !s.IsAutoDiscovered),
                ActionHandlerCount = viewModels.Count(s => s.HandlerType == null),
                TypeHandlerCount = viewModels.Count(s => s.HandlerType != null),
                TopEventTypes = viewModels
                    .GroupBy(s => s.EventType)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new EventTypeCount
                    {
                        EventType = g.Key,
                        EventTypeShortName = g.First().EventTypeShortName,
                        Count = g.Count()
                    })
                    .ToList(),
                TopTopics = viewModels
                    .GroupBy(s => s.TopicName)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new TopicCount
                    {
                        TopicName = g.Key,
                        Count = g.Count()
                    })
                    .ToList()
            };

            stats.UnhealthyTopics = _topicStatusStore.GetAll()
                .Count(t => t.State is TopicSubscriptionRuntimeState.Recovering or TopicSubscriptionRuntimeState.Failed);

            logger.LogDebug("Generated statistics: Total={Total}, Active={Active}, Unhealthy={Unhealthy}",
                stats.TotalSubscriptions, stats.ActiveSubscriptions, stats.UnhealthySubscriptions);

            return Res.Ok(stats);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to calculate statistics");
            return Res.Fail(localizer["Services:Monitor:StatisticsFailed", ex.GetMessageRecursively()]);
        }
    }

    #endregion

    #region Lifecycle Methods

    /// <summary>
    /// Activate subscription
    /// </summary>
    public async Task<Res> ActivateSubscriptionAsync(EventSubscriptionId subscriptionId)
    {
        try
        {
            var subscription = _subscriptionManager.GetById(subscriptionId);

            if (subscription == null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            if (subscription.State == EventSubscriptionState.Active)
            {
                return Res.Fail(localizer["Services:Monitor:AlreadyActive"]);
            }

            if (subscription.State == EventSubscriptionState.Disposed)
            {
                return Res.Fail(localizer["Services:Monitor:CannotActivateDisposed"]);
            }

            if (subscription.Activation != Monica.Core.TypeDiscovery.Abstractions.ActivationMode.Automatic)
                return Res.Fail(localizer["Services:Monitor:ActivationHeld"]);

            if (subscription.State == EventSubscriptionState.Inactive)
            {
                await _subscriptionManager.ReactivateAsync(subscriptionId);
            }
            else
            {
                await _subscriptionManager.ActivateAsync(subscriptionId);
            }

            logger.LogInformation("Activated subscription: {EventSubscriptionId}", subscriptionId);
            return Res.Ok(localizer["Services:Monitor:Activated"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to activate subscription: {EventSubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Monitor:ActivateFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Deactivate subscription
    /// </summary>
    public async Task<Res> DeactivateSubscriptionAsync(EventSubscriptionId subscriptionId)
    {
        try
        {
            var subscription = _subscriptionManager.GetById(subscriptionId);

            if (subscription == null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            if (subscription.State != EventSubscriptionState.Active)
            {
                return Res.Fail(localizer["Services:Monitor:OnlyActiveCanDeactivate", subscription.State]);
            }

            await _subscriptionManager.DeactivateAsync(subscriptionId);

            logger.LogInformation("Deactivated subscription: {EventSubscriptionId}", subscriptionId);
            return Res.Ok(localizer["Services:Monitor:Deactivated"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deactivate subscription: {EventSubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Monitor:DeactivateFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Remove subscription
    /// </summary>
    public async Task<Res> UnsubscribeAsync(EventSubscriptionId subscriptionId)
    {
        try
        {
            var subscription = _subscriptionManager.GetById(subscriptionId);

            if (subscription == null)
            {
                return Res.Fail(localizer["Services:Common:SubscriptionNotFound"]);
            }

            if (subscription.State == EventSubscriptionState.Disposed)
            {
                return Res.Fail(localizer["Services:Monitor:AlreadyRemoved"]);
            }

            await _subscriptionManager.UnsubscribeAsync(subscriptionId);

            logger.LogInformation("Removed subscription: {EventSubscriptionId}", subscriptionId);
            return Res.Ok(localizer["Services:Monitor:Removed"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to unsubscribe: {EventSubscriptionId}", subscriptionId);
            return Res.Fail(localizer["Services:Monitor:RemoveFailed", ex.GetMessageRecursively()]);
        }
    }

    #endregion

    #region Real-time Subscription

    /// <summary>
    /// Opens an independently owned change feed for one monitor consumer.
    /// </summary>
    /// <param name="cancellationToken">Optionally retires the feed when the consumer lifetime is cancelled.</param>
    /// <returns>A lease that the caller must dispose when its view retires.</returns>
    /// <remarks>
    /// Source observation is lazy. A feed receives only changes observed after it opens; it does not replay history.
    /// Each feed retains at most 256 unread notifications and drops its oldest notification when full.
    /// Inspect <see cref="EventBusMonitorChangeFeed.DroppedChangeCount"/> and refresh the registry after a gap;
    /// an observed change list must not be presented as complete history. A cancelled or disposed feed stops accepting
    /// notifications; buffered notifications remain drainable. The caller must cancel and await its own reader loop.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The host has already disposed the monitoring service.</exception>
    /// <exception cref="OperationCanceledException">The supplied token was already cancelled.</exception>
    public EventBusMonitorChangeFeed OpenChangeFeed(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EventBusMonitorChangeFeed feed;
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureInitialized();
            var id = Guid.NewGuid();
            feed = new EventBusMonitorChangeFeed(() => RetireFeed(id));
            _changeFeeds.Add(id, feed);
        }

        feed.AttachCancellation(cancellationToken);
        return feed;
    }

    /// <summary>
    /// Opens a separately buffered change reader whose lease is retired by the supplied lifetime token.
    /// </summary>
    /// <param name="cancellationToken">A cancellable consumer-lifetime token; cancellation releases the reader lease.</param>
    /// <returns>The consumer's own change reader.</returns>
    /// <remarks>
    /// This reader adapter requires a cancellable token because it does not return a disposable lease.
    /// Prefer <see cref="OpenChangeFeed"/> to inspect notification loss and retire the feed explicitly.
    /// It has the same transient, bounded-buffer contract as that method and never shares readers between views.
    /// </remarks>
    /// <exception cref="ArgumentException">The token cannot be cancelled.</exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The monitoring service has been disposed.</exception>
    public ChannelReader<EventBusMonitorChange> SubscribeToChanges(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            throw new ArgumentException("A consumer lifetime token is required to own the reader lease.", nameof(cancellationToken));
        }

        return OpenChangeFeed(cancellationToken).Reader;
    }

    /// <summary>
    /// Get the runtime status snapshots of all known topic subscriptions.
    /// </summary>
    public Res<List<TopicSubscriptionStatus>> GetAllTopicStatuses()
    {
        try
        {
            return Res.Ok(_topicStatusStore.GetAll().ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get topic statuses");
            return Res.Fail(localizer["Services:Monitor:GetTopicStatusesFailed", ex.GetMessageRecursively()], ResStatus.InternalError);
        }
    }

    #endregion

    #region Mapping Methods

    /// <summary>
    /// Map IEventSubscription to ViewModel
    /// </summary>
    private SubscriptionViewModel MapToViewModel(IEventSubscription subscription)
    {
        var vm = new SubscriptionViewModel
        {
            SubscriptionId = subscription.Id.ToString(),
            EventType = subscription.EventType.GetCleanFullName(),
            EventTypeShortName = subscription.EventType.GetCleanName(),
            TopicName = subscription.TopicName,
            ServiceKey = subscription.ServiceKey,
            HandlerType = subscription.HandlerType?.GetCleanFullName(),
            HandlerTypeShortName = subscription.HandlerType?.GetCleanName(),
            HandlerFactoryType = subscription.HandlerFactory.GetType().Name,
            Scope = subscription.Scope,
            State = subscription.State,
            Activation = subscription.Activation,
            CreatedAt = subscription.CreatedAt,
            ActivatedAt = subscription.ActivatedAt,
            DeactivatedAt = subscription.DeactivatedAt,
            IsAutoDiscovered = subscription.IsAutoDiscovered,
            Metadata = subscription.Metadata
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? "null")
        };

        // Extract Action handler metadata
        if (subscription.HandlerType == null)
        {
            vm.ActionMethodName = subscription.GetMetadata<string>(SubscriptionMetadataKeys.ActionMethodName);
            vm.ActionDeclaringType = subscription.GetMetadata<string>(SubscriptionMetadataKeys.ActionDeclaringType);
            vm.ActionMethodSignature = subscription.GetMetadata<string>(SubscriptionMetadataKeys.ActionMethodSignature);
            vm.ActionIsStatic = subscription.GetMetadata<bool?>(SubscriptionMetadataKeys.ActionIsStatic);
        }

        // Distributed subscriptions join the runtime health reported by their provider
        if (subscription.Scope == EventSubscriptionScope.Distributed)
        {
            vm.TopicStatus = _topicStatusStore.Get(subscription.ServiceKey, subscription.TopicName);
        }

        return vm;
    }

    private List<IEventSubscription> GetVisibleSubscriptions()
    {
        return _subscriptionManager.GetAll()
            .Where(subscription => !EventBusTestMetadataKeys.IsTestListenerSubscription(subscription))
            .ToList();
    }

    /// <summary>
    /// Map EventSubscriptionChange to ViewModel
    /// </summary>
    private SubscriptionChangeViewModel MapToChangeViewModel(EventSubscriptionChange change)
    {
        return new SubscriptionChangeViewModel
        {
            ChangeType = change.ChangeType,
            Subscription = MapToViewModel(change.Subscription),
            Timestamp = change.Timestamp
        };
    }

    #endregion

    #region Disposal

    /// <summary>Retires all change feeds and source observations when the owning host shuts down.</summary>
    /// <returns>A completed disposal operation; repeated calls are harmless.</returns>
    public ValueTask DisposeAsync()
    {
        IDisposable? registryObserver;
        List<EventBusMonitorChangeFeed> feeds;
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            registryObserver = _registryObserver;
            _registryObserver = null;
            _topicStatusStore.StatusChanged -= OnTopicStatusChanged;
            feeds = _changeFeeds.Values.ToList();
            _changeFeeds.Clear();
        }

        foreach (var feed in feeds)
        {
            feed.Dispose();
        }

        registryObserver?.Dispose();
        logger.LogInformation("EventBusMonitorService disposed");
        return ValueTask.CompletedTask;
    }

    #endregion
}

/// <summary>
/// Owns a bounded, independent stream of transient EventBus monitoring notifications for one consumer.
/// </summary>
/// <remarks>
/// The consumer owns disposal. Disposal unregisters the feed and completes its writer but does not wait for or
/// cancel a consumer's reader loop. Notifications are read-only snapshots and must not be mutated by readers.
/// The feed is safe to dispose repeatedly, including after the host retires it.
/// </remarks>
public sealed class EventBusMonitorChangeFeed : IDisposable
{
    private const int BUFFER_CAPACITY = 256;
    private readonly Channel<EventBusMonitorChange> _channel;
    private readonly object _cancellationLock = new();
    private Action? _onRetired;
    private CancellationTokenRegistration _cancellationRegistration;
    private long _droppedChangeCount;
    private int _retired;

    internal EventBusMonitorChangeFeed(Action onRetired)
    {
        _onRetired = onRetired;
        _channel = Channel.CreateBounded<EventBusMonitorChange>(
            new BoundedChannelOptions(BUFFER_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            },
            _ => Interlocked.Increment(ref _droppedChangeCount));
    }

    /// <summary>Gets this feed's own reader; consumer cancellation belongs to its reader loop.</summary>
    public ChannelReader<EventBusMonitorChange> Reader => _channel.Reader;

    /// <summary>
    /// Gets the cumulative number of notifications dropped from this feed because its unread buffer was full.
    /// A nonzero value means the consumer's observed changes are incomplete and it must refresh current snapshots.
    /// </summary>
    public long DroppedChangeCount => Interlocked.Read(ref _droppedChangeCount);

    internal void TryPublish(EventBusMonitorChange change)
    {
        if (Volatile.Read(ref _retired) == 0)
        {
            _channel.Writer.TryWrite(change);
        }
    }

    internal void AttachCancellation(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return;
        }

        var registration = cancellationToken.Register(static state => ((EventBusMonitorChangeFeed)state!).Dispose(), this);
        lock (_cancellationLock)
        {
            if (Volatile.Read(ref _retired) != 0)
            {
                registration.Unregister();
            }
            else
            {
                _cancellationRegistration = registration;
            }
        }
    }

    /// <summary>Stops accepting new notifications and releases this consumer's registration.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _onRetired, null)?.Invoke();
        _channel.Writer.TryComplete();
        lock (_cancellationLock)
        {
            // Unregister is nonblocking even when disposal runs from this registration's own callback.
            _cancellationRegistration.Unregister();
            _cancellationRegistration = default;
        }
    }
}
