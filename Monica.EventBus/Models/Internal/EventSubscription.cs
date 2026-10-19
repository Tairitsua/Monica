using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;

namespace Monica.EventBus.Models.Internal;

/// <summary>
/// Concrete subscription implementation with lifecycle management.
/// </summary>
internal class EventSubscription(EventSubscriptionDescriptor descriptor) : IEventSubscription
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private volatile EventSubscriptionState _state = EventSubscriptionState.Pending;

    #region Identity

    public EventSubscriptionId Id { get; } = EventSubscriptionId.NewId();
    public string? ServiceKey { get; } = descriptor.ServiceKey;
    public string? WorkloadOwner { get; } = descriptor.WorkloadOwner;
    public Type EventType { get; } = descriptor.EventType;
    public string TopicName { get; } = descriptor.TopicName;

    #endregion

    #region Handler Information

    public Type? HandlerType => descriptor.HandlerFactory.GetHandlerType();
    public IEventHandlerFactory HandlerFactory { get; } = descriptor.HandlerFactory;

    #endregion

    #region Scope Information

    public EventSubscriptionScope Scope { get; } = descriptor.Scope;

    #endregion

    #region Lifecycle

    public EventSubscriptionState State => _state;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; private set; }
    public DateTime? DeactivatedAt { get; private set; }
    public bool IsAutoDiscovered { get; } = descriptor.IsAutoDiscovered;

    #endregion

    #region Metadata

    public IReadOnlyDictionary<string, object> Metadata { get; } = descriptor.Metadata ?? new Dictionary<string, object>();

    public T? GetMetadata<T>(string key)
    {
        return Metadata.TryGetValue(key, out var value) && value is T typedValue
            ? typedValue
            : default;
    }

    #endregion

    #region State Transitions

    public Task ActivateAsync()
        => ActivateAsync(static _ => { }, CancellationToken.None);

    internal async Task ActivateAsync(
        Action<EventSubscription> onCommitted,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != EventSubscriptionState.Pending && _state != EventSubscriptionState.Inactive)
            {
                throw new InvalidOperationException($"Cannot activate subscription in state {_state}");
            }

            _state = EventSubscriptionState.Active;
            ActivatedAt = DateTime.UtcNow;
            onCommitted(this);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task DeactivateAsync()
        => DeactivateAsync(static _ => { }, CancellationToken.None);

    internal async Task DeactivateAsync(
        Action<EventSubscription> onCommitted,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != EventSubscriptionState.Active)
            {
                throw new InvalidOperationException($"Cannot deactivate subscription in state {_state}");
            }

            _state = EventSubscriptionState.Inactive;
            DeactivatedAt = DateTime.UtcNow;
            onCommitted(this);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task ReactivateAsync() => ActivateAsync();

    #endregion

    public ValueTask DisposeAsync()
        => DisposeAsync(static _ => { }, CancellationToken.None);

    internal async ValueTask DisposeAsync(
        Action<EventSubscription> onCommitted,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == EventSubscriptionState.Disposed)
            {
                onCommitted(this);
                return;
            }

            if (HandlerFactory is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (HandlerFactory is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _state = EventSubscriptionState.Disposed;
            onCommitted(this);
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
