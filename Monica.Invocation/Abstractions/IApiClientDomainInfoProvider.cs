

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Resolves domain dependency flags and the target application identifier for each dependent API domain.
/// </summary>
public interface IApiClientDomainInfoProvider
{
    /// <summary>Returns a flags enum selecting the domains consumed by this host.</summary>
    /// <remarks>Its enum names must match ApiClientDomain declarations. Application-model routing uses compiled operation ownership instead.</remarks>
    object GetDependencyDomains();

    /// <summary>Resolves logical identity, display name, and the private destination for one dependent domain.</summary>
    Monica.Invocation.Models.ApiServiceDescriptor GetDomain(Enum domain);
}
