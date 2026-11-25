using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.ApplicationRoles;

namespace Monica.Core.HostedService.Services.Support;

/// <summary>
/// Retains the host-owned service collection so startup validation sees registrations added after this module runs.
/// </summary>
internal sealed class HostedServiceDescriptorCatalog(IServiceCollection services, MonicaCompositionState composition)
{
    internal string? GetRoleId(ServiceDescriptor descriptor, Type implementation, IRoleHealthStateProvider health) =>
        composition.GetAdmittedHostedLifecycle(descriptor) is null
            ? health.GetRoleId(implementation, composition.GetServiceRegistrationOwner(descriptor)) : null;

    public IReadOnlyList<ServiceDescriptor> GetDescriptors()
    {
        return services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToArray();
    }
}
