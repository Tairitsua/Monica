using Monica.Core.ApplicationRoles;

namespace Monica.Core.Mediator;

/// <summary>Freezes handler ownership from the final unkeyed DI registrations, without constructing a handler.</summary>
internal sealed class MediatorHandlerAdmission
{
    private readonly Dictionary<Type, (Type Implementation, Type? RegistrationOwner)> _handlers = [];

    // Last registration wins. A healthy closed registration also supersedes a faulted open generic.
    internal void Record(Type service, Type implementation, Type? registrationOwner) =>
        _handlers[service] = (implementation, registrationOwner);

    internal void EnsureAvailable(Type closedService, Monica.Core.TypeDiscovery.Abstractions.ITypeActivationPolicy activation)
    {
        if (!_handlers.TryGetValue(closedService, out var handler)
            && !_handlers.TryGetValue(closedService.GetGenericTypeDefinition(), out handler)) return;
        if (activation.GetFaultedRequestHandlerRole(handler.Implementation, handler.RegistrationOwner) is { } role)
            throw new RoleUnavailableException(role);
    }
}
