

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Resolves domain dependency flags and the target application identifier for each dependent RPC domain.
/// </summary>
public interface IRpcClientDomainInfoProvider
{
    object GetDependencyDomains();

    /// <summary>Resolves logical identity, display name, and the private destination for one dependent domain.</summary>
    Monica.Rpc.Models.RpcServiceDescriptor GetDomain(Enum domain);
}
