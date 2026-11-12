using Monica.ApplicationModel.Models;
using Monica.Core;
using Monica.Modules;

namespace Monica.ServiceDiscovery.Services.Support;

/// <summary>Preserves the deployment map's identity authority when discovery is composed with an application model.</summary>
internal static class ServiceDiscoveryApplicationIdentity
{
    internal static void Validate(ModuleServiceDiscoveryOption options, ApplicationDeployment deployment)
    {
        if (MonicaApplicationOptions.Normalize(options.AppId) is { } appId && appId != deployment.AppId)
            throw new InvalidOperationException("Service discovery AppId must match Application:AppId.");
        if (MonicaApplicationOptions.Normalize(options.AppName) is { } appName && appName != deployment.AppName)
            throw new InvalidOperationException("Service discovery AppName must match the current Deployment:Applications entry.");

        var identities = deployment.Applications.Keys.ToDictionary(id => id, StringComparer.OrdinalIgnoreCase);
        var predefined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in options.PredefinedServices)
        {
            if (string.IsNullOrWhiteSpace(service.AppId) || service.AppId != service.AppId.Trim())
                throw new InvalidOperationException("Predefined service AppId must be nonempty without surrounding whitespace.");
            if (!predefined.Add(service.AppId))
                throw new InvalidOperationException($"Predefined service AppId '{service.AppId}' is declared more than once.");
            if (identities.TryGetValue(service.AppId, out var canonicalId) && canonicalId != service.AppId)
                throw new InvalidOperationException(
                    $"Predefined service AppId '{service.AppId}' conflicts with deployment AppId '{canonicalId}' under case-insensitive configuration lookup.");
            if (deployment.Applications.TryGetValue(service.AppId, out var entry)
                && MonicaApplicationOptions.Normalize(service.AppName) is { } name && name != entry.AppName)
                throw new InvalidOperationException(
                    $"Predefined service '{service.AppId}' must use the AppName from Deployment:Applications.");
        }
    }
}
