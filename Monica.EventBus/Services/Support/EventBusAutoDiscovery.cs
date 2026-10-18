using Microsoft.Extensions.DependencyInjection;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Models.Internal;

namespace Monica.EventBus.Services.Support;

internal sealed class EventBusAutoDiscovery
{
    private readonly List<EventHandlerRegistration> _registrations = [];

    public void Collect(Type type)
    {
        if (type is not { IsClass: true, IsAbstract: false } || !typeof(IEventHandler).IsAssignableFrom(type))
        {
            return;
        }

        _registrations.AddRange(EventHandlerRegistration.CreateFromHandlerType(type));
    }

    public IReadOnlyList<EventSubscriptionDescriptor> BuildDescriptors(
        IServiceScopeFactory serviceScopeFactory,
        IServiceProviderIsService? registeredServices,
        ITypeActivationPolicy? activation = null)
    {
        return _registrations
            .Where(static registration => registration.IsAutoRegistered)
            .Where(registration => !registration.IsDistributed ||
                activation?.IsEnabled(registration.HandlerType, TypeActivationKind.ExternalSubscription) != false)
            // Type discovery sees handler classes inside shared assemblies that this host references but
            // does not own. Only subscribe handler types this host registered; subscribing a foreign host's
            // handler fails on every delivery and wedges the topic subscription in endless retry.
            // Inspect registration metadata without constructing handlers or their scoped dependencies.
            // Activation failures and asynchronous disposal belong to the delivery-owned scope.
            .Where(registration => registeredServices?.IsService(registration.HandlerType)
                ?? throw new InvalidOperationException(
                    "EventBus automatic discovery requires IServiceProviderIsService registration metadata from the host container."))
            .Select(registration => new EventSubscriptionDescriptor
            {
                ServiceKey = null,
                EventType = registration.EventType,
                TopicName = registration.TopicName,
                HandlerFactory = new IocEventHandlerFactory(serviceScopeFactory, registration.HandlerType),
                Scope = registration.IsDistributed ? EventSubscriptionScope.Distributed : EventSubscriptionScope.Local,
                IsAutoDiscovered = true
            })
            .ToList();
    }
}
