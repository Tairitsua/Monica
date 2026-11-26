using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.Tool.Extensions;

namespace Monica.EventBus.Services.Support;

internal delegate Task EventHandlerMethodExecutorAsync(
    IEventHandler target,
    object parameter,
    IEventSubscription subscription,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken);

internal interface IEventHandlerMethodExecutor
{
    EventHandlerMethodExecutorAsync ExecutorAsync { get; }
}

internal sealed class EventHandlerMethodExecutor<TEvent> : IEventHandlerMethodExecutor
    where TEvent : class
{
    public EventHandlerMethodExecutorAsync ExecutorAsync =>
        EventHandlerExecutionAdapter<TEvent>.ExecuteAsync;
}

internal static class EventHandlerExecutionAdapter<TEvent>
    where TEvent : class
{
    public static async Task ExecuteAsync(
        IEventHandler target,
        object parameter,
        IEventSubscription subscription,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (parameter is not TEvent eventData)
        {
            throw new ArgumentException(
                $"Event payload type '{parameter.GetType().FullName}' does not match '{typeof(TEvent).FullName}'.",
                nameof(parameter));
        }

        var handlerType = target.GetType();
        var contract = subscription.Scope == EventSubscriptionScope.Local
            ? typeof(ILocalEventHandler<TEvent>)
            : typeof(IDistributedEventHandler<TEvent>);
        var point = subscription.Scope == EventSubscriptionScope.Local
            ? EventBusExecutionPoints.LocalHandler
            : EventBusExecutionPoints.DistributedHandler;
        var descriptor = ExecutionDescriptor.ForInterface<TEvent, ExecutionUnit>(
            point,
            handlerType,
            contract,
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var features = new ExecutionFeatureCollection();
        features.Set(new EventHandlerExecutionFeature(
            subscription.Id,
            subscription.TopicName,
            subscription.Scope,
            subscription.ServiceKey,
            subscription.IsAutoDiscovered) { ReceiverInstanceId = subscription.ReceiverInstanceId });
        await serviceProvider.GetRequiredService<IExecutionPipeline>()
            .ExecuteAsync(
                descriptor,
                eventData,
                target,
                () => InvokeHandlerAsync(target, eventData, subscription.Scope, cancellationToken),
                cancellationToken,
                features)
            .ConfigureAwait(false);
    }

    private static Task InvokeHandlerAsync(
        IEventHandler handler,
        TEvent eventData,
        EventSubscriptionScope scope,
        CancellationToken cancellationToken)
    {
        return scope switch
        {
            EventSubscriptionScope.Local =>
                ((ILocalEventHandler<TEvent>)handler).HandleEventAsync(eventData, cancellationToken),
            EventSubscriptionScope.Distributed =>
                ((IDistributedEventHandler<TEvent>)handler).HandleEventAsync(eventData, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported event subscription scope.")
        };
    }
}
