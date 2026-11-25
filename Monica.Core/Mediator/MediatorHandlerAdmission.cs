using Monica.Core.ApplicationRoles;

namespace Monica.Core.Mediator;

/// <summary>Freezes handler ownership from the final unkeyed DI registrations, without constructing a handler.</summary>
internal sealed class MediatorHandlerAdmission
{
    private readonly Dictionary<Type, string?> _faultedRoles = [];

    // Last registration wins. A healthy closed registration also supersedes a faulted open generic.
    internal void Record(Type service, string? faultedRole) => _faultedRoles[service] = faultedRole;

    internal void EnsureAvailable(Type closedService)
    {
        if (!_faultedRoles.TryGetValue(closedService, out var role))
            _faultedRoles.TryGetValue(closedService.GetGenericTypeDefinition(), out role);
        if (role is not null) throw new RoleUnavailableException(role);
    }
}
