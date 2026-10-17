namespace Monica.Rpc.Annotations;

/// <summary>
/// Declares which dependency domain a concrete RPC client implementation belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Monica.Modules.ModuleRpcClient"/> uses this attribute during automatic registration instead of inferring the domain
/// from the implementation type name.
/// </para>
/// <para>
/// Manual RPC client implementations must apply this attribute explicitly to participate in automatic registration.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RpcClientDomainAttribute(string domainName) : Attribute
{
    /// <summary>
    /// Gets the dependency domain name that should match an enum member returned by
    /// <see cref="Monica.Modules.IRpcClientDomainInfoProvider.GetDependencyDomains"/>.
    /// </summary>
    public string DomainName { get; } = string.IsNullOrWhiteSpace(domainName)
        ? throw new ArgumentException("RPC client domain name can not be null or whitespace.", nameof(domainName))
        : domainName;
}
