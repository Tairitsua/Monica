using System.Reflection;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Annotations;

namespace Monica.Core.TypeDiscovery.Services;

internal sealed class StandaloneTypeActivationPolicy(bool autonomousWork) : ITypeActivationPolicy
{
    public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null)
    {
        ArgumentNullException.ThrowIfNull(implementationType);
        if (kind is TypeActivationKind.Service or TypeActivationKind.RequestHandler or TypeActivationKind.Endpoint)
            return ActivationMode.Automatic;
        if (kind is TypeActivationKind.Seeder or TypeActivationKind.HostedService
            && implementationType.IsDefined(typeof(ProcessLocalAttribute), inherit: false))
            return ActivationMode.Automatic;
        return autonomousWork ? ActivationMode.Automatic : ActivationMode.Manual;
    }

    public void EnsureCanExecuteRequest(Type requestType) { }
    public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
}
