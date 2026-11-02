using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services.Support;

internal sealed class EventBatchHandlerMethodExecutor<TEvent> : IEventHandlerMethodExecutor where TEvent : class
{
    public EventHandlerMethodExecutorAsync ExecutorAsync => ExecuteAsync;

    private static async Task ExecuteAsync(IEventHandler target, object parameter, IEventSubscription subscription,
        IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        if (parameter is not IReadOnlyList<object> values)
            throw new ArgumentException("Batch delivery requires an ordered payload list.", nameof(parameter));
        var payloads = values.Cast<TEvent>().ToArray();
        var batch = serviceProvider.GetRequiredService<EventBatchDeliveryContext>().Feature
            ?? throw new InvalidOperationException("Batch delivery metadata is missing.");
        if (payloads.Length != batch.Metadata.Count)
            throw new InvalidOperationException("Batch payloads and delivery metadata must be aligned.");
        var descriptor = ExecutionDescriptor.ForInterface<IReadOnlyList<TEvent>, ExecutionUnit>(
            EventBusExecutionPoints.DistributedBatchHandler, target.GetType(),
            typeof(IDistributedBatchEventHandler<TEvent>), isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var features = new ExecutionFeatureCollection();
        features.Set(batch);
        features.Set(new EventHandlerExecutionFeature(subscription.Id, subscription.TopicName,
            subscription.Scope, subscription.ServiceKey, subscription.IsAutoDiscovered));
        await serviceProvider.GetRequiredService<IExecutionPipeline>().ExecuteAsync(
            descriptor, (IReadOnlyList<TEvent>)payloads, target,
            () => batch.Indices.Count == 0 ? Task.CompletedTask
                : ((IDistributedBatchEventHandler<TEvent>)target).HandleBatchAsync(
                    Array.AsReadOnly(batch.Indices.Select(index => payloads[index]).ToArray()), cancellationToken),
            cancellationToken, features).ConfigureAwait(false);
    }
}
