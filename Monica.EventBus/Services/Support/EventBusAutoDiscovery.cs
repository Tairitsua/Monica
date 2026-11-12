using Microsoft.Extensions.DependencyInjection;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Models.Internal;

namespace Monica.EventBus.Services.Support;

internal sealed class EventBusAutoDiscovery
{
    private readonly List<EventHandlerRegistration> _registrations = [];
    private readonly HashSet<Type> _collectedTypes = [];
    private readonly Dictionary<Type, Type?> _registrationOwners = [];

    internal void CollectRegisteredHandlers(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        // Assembly scanning contains generic templates, not the concrete closures selected by composition.
        // Inspect exposed concrete service types only; do not construct factories or infer interface targets.
        foreach (var type in descriptors.Where(descriptor => !descriptor.IsKeyedService)
                     .Select(descriptor => descriptor.ServiceType).Where(type => type.IsConstructedGenericType).Distinct())
            Collect(type);
    }

    internal void CaptureRegistrationOwners(IReadOnlyList<ServiceDescriptor> descriptors,
        Func<ServiceDescriptor, Type?> getOwner)
    {
        foreach (var handler in _registrations.Where(registration => registration.IsAutoRegistered && registration.IsDistributed)
                     .Select(registration => registration.HandlerType).Distinct())
        {
            var owners = descriptors.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == handler)
                .Select(getOwner).Distinct().ToArray();
            if (owners.Length > 1)
                throw new InvalidOperationException(
                    $"Distributed event handler '{handler.FullName}' has conflicting registration owners. Register the shared handler under one application declaration.");
            if (owners.Length == 1) _registrationOwners.Add(handler, owners[0]);
        }
    }

    public void Collect(Type type)
    {
        if (type is not { IsClass: true, IsAbstract: false } || type.ContainsGenericParameters
            || !typeof(IEventHandler).IsAssignableFrom(type) || !_collectedTypes.Add(type))
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
                IsAutoDiscovered = true,
                RegistrationOwner = registration.IsDistributed ? _registrationOwners.GetValueOrDefault(registration.HandlerType) : null
            })
            .ToList();
    }
}
