using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Core.Execution;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;

namespace Monica.Configuration.EventBus.Services;

internal sealed class ConfigurationReloadEventHandler(IConfigurationReloadSignalReceiver receiver)
    : IDistributedEventHandler<ConfigurationReloadSignal>
{
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public Task HandleEventAsync(ConfigurationReloadSignal eventData, CancellationToken cancellationToken)
        => receiver.ReceiveAsync(eventData, cancellationToken);
}

internal sealed class ConfigurationReloadEventHandlerFactory(
    IServiceScopeFactory serviceScopeFactory,
    IConfigurationReloadSignalReceiver receiver) : IEventHandlerFactory
{
    public ValueTask<IEventHandlerExecutionScope> CreateExecutionScopeAsync()
    {
        var scope = serviceScopeFactory.CreateAsyncScope();
        return ValueTask.FromResult<IEventHandlerExecutionScope>(new EventHandlerExecutionScope(
            new ConfigurationReloadEventHandler(receiver), scope.ServiceProvider, scope.DisposeAsync));
    }

    // The handler belongs exclusively to this explicit subscription. Registering its concrete type in DI would
    // also make it eligible for EventBus automatic discovery on the event's default topic.
    public Type GetHandlerType() => typeof(ConfigurationReloadEventHandler);
}
