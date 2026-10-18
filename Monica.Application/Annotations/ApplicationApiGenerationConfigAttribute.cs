using Monica.Invocation.Models;

namespace Monica.Application.Annotations;

/// <summary>Selects generated API client adapters and owned receivers in a host or adapter assembly.</summary>
/// <param name="transport">Local for in-process collaboration, or Http for remote collaboration. Grpc is unsupported.</param>
/// <param name="contractAssemblies">Marker types identifying assemblies containing published requests and their generated interfaces.</param>
/// <remarks>
/// Interfaces are always generated beside their requests, without this attribute. Local generates no network
/// receivers unless the host also opts into WebApiGenerationConfig. Http generates matching receivers for owned
/// handlers. An external UI can omit handler assemblies and generate clients only. Runtime registration still
/// selects the same transport through AddApiClient. Configure type discovery and application execution separately.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ApplicationApiGenerationConfigAttribute(ApiClientTransportKind transport, params Type[] contractAssemblies) : Attribute
{
    /// <summary>Gets the single client transport generated for this assembly.</summary>
    public ApiClientTransportKind Transport { get; } = transport;

    /// <summary>Gets markers for the contract assemblies consumed by this host. The current assembly is also inspected.</summary>
    public Type[] ContractAssemblies { get; } = contractAssemblies;

    /// <summary>Identifies referenced handler assemblies owned by this host. Source handlers are included automatically.</summary>
    public Type[] HandlerAssemblies { get; set; } = [];

    /// <summary>Overrides the HTTP client base type; it must derive from HttpApiClient and expose its constructor.</summary>
    public Type? HttpClientBaseType { get; set; }

    /// <summary>Overrides the Local client base type; it must derive from LocalApiClient and expose its constructor.</summary>
    public Type? LocalClientBaseType { get; set; }
}
