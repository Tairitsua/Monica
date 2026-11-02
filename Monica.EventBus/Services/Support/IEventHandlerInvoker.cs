using System.Collections.Concurrent;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services.Support;

/// <summary>
/// Invokes a resolved event handler for a runtime event type.
/// </summary>
public interface IEventHandlerInvoker
{
    /// <summary>
    /// Invokes the exact local or distributed handler contract selected by the subscription.
    /// </summary>
    /// <param name="eventHandler">The resolved handler instance.</param>
    /// <param name="eventData">The event payload, or an IReadOnlyList&lt;object&gt; for an explicit batch subscription.</param>
    /// <param name="eventType">The exact event type used for dispatch.</param>
    /// <param name="subscription">The subscription that selected the handler contract.</param>
    /// <param name="serviceProvider">The scope that owns the handler and pipeline behaviors.</param>
    /// <param name="cancellationToken">Signals that the caller no longer needs the handler result.</param>
    Task InvokeAsync(
        IEventHandler eventHandler,
        object eventData,
        Type eventType,
        IEventSubscription subscription,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Invokes strongly typed event-handler methods while caching their runtime adapters.
/// </summary>
public sealed class EventHandlerInvoker : IEventHandlerInvoker
{
    private readonly ConcurrentDictionary<(Type HandlerType, Type EventType), HandlerExecutors> _cache = new();

    /// <inheritdoc />
    public async Task InvokeAsync(
        IEventHandler eventHandler,
        object eventData,
        Type eventType,
        IEventSubscription subscription,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        var executors = _cache.GetOrAdd(
            (eventHandler.GetType(), eventType),
            static key => CreateExecutors(key.HandlerType, key.EventType));

        var executor = subscription.Scope switch
        {
            EventSubscriptionScope.Local => executors.Local,
            EventSubscriptionScope.Distributed => executors.Distributed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(subscription), subscription.Scope, "Unsupported event subscription scope.")
        };

        if (executor is null)
        {
            throw new InvalidOperationException(
                $"Event handler '{eventHandler.GetType().AssemblyQualifiedName}' does not implement the " +
                $"{subscription.Scope.ToString().ToLowerInvariant()} contract for event '{eventType.FullName}'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await executor.ExecutorAsync(
            eventHandler,
            eventData,
            subscription,
            serviceProvider,
            cancellationToken);
    }

    private static HandlerExecutors CreateExecutors(Type handlerType, Type eventType)
    {
        return new HandlerExecutors(
            CreateExecutorIfImplemented(
                handlerType,
                typeof(ILocalEventHandler<>).MakeGenericType(eventType),
                eventType),
            CreateExecutorIfImplemented(
                handlerType,
                typeof(IDistributedBatchEventHandler<>).MakeGenericType(eventType),
                eventType, batch: true)
            ?? CreateExecutorIfImplemented(
                handlerType, typeof(IDistributedEventHandler<>).MakeGenericType(eventType), eventType));
    }

    private static IEventHandlerMethodExecutor? CreateExecutorIfImplemented(
        Type handlerType,
        Type handlerContract,
        Type eventType, bool batch = false)
    {
        if (!handlerContract.IsAssignableFrom(handlerType))
        {
            return null;
        }

        return (IEventHandlerMethodExecutor?)Activator.CreateInstance(
            (batch ? typeof(EventBatchHandlerMethodExecutor<>) : typeof(EventHandlerMethodExecutor<>)).MakeGenericType(eventType));
    }

    private sealed record HandlerExecutors(
        IEventHandlerMethodExecutor? Local,
        IEventHandlerMethodExecutor? Distributed);
}
