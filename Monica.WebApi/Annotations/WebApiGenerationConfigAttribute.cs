using Monica.Invocation.Abstractions;

namespace Monica.WebApi.Annotations;

/// <summary>
/// Opts a host into HTTP receiver generation. Legacy request assemblies can also select API client targets.
/// </summary>
/// <param name="routePrefix">
/// The route prefix placed before the domain and request route, such as <c>api/v1</c>.
/// Leading and trailing slashes are not permitted.
/// </param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class WebApiGenerationConfigAttribute(string routePrefix = "api/v1") : Attribute
{
    /// <summary>
    /// Gets the route prefix placed before the domain and request route.
    /// </summary>
    public string RoutePrefix { get; } = routePrefix;

    /// <summary>
    /// Gets or sets the domain used by local request contracts. For published requests, this value,
    /// when supplied, must match the domain derived from the published-language namespace.
    /// </summary>
    public string? DomainName { get; set; }

    /// <summary>
    /// Gets or sets the transport implementations generated for published API contracts.
    /// The API interfaces are generated even when this value is <see cref="ApiClientGenerationTargets.None"/>.
    /// For host-owned generation use ApplicationApiGenerationConfig instead; do not emit duplicate adapters in Protocol.
    /// </summary>
    public ApiClientGenerationTargets ApiClientTargets { get; set; }

    /// <summary>
    /// Gets or sets the base type for generated HTTP clients. The default is <see cref="HttpApiClient"/>.
    /// A custom type must inherit <see cref="HttpApiClient"/> and expose the same constructor dependencies.
    /// </summary>
    public Type? HttpClientBaseType { get; set; }

    /// <summary>
    /// Gets or sets the base type for generated local clients. The default is <see cref="LocalApiClient"/>.
    /// A custom type must inherit <see cref="LocalApiClient"/> and expose the same constructor dependencies.
    /// </summary>
    public Type? LocalClientBaseType { get; set; }

    /// <summary>Identifies referenced handler assemblies owned by this publishing host. Source handlers are included automatically.</summary>
    /// <remarks>With ApplicationApiGenerationConfig, its HandlerAssemblies selection also applies. This configuration never publishes every referenced contract.</remarks>
    public Type[] HandlerAssemblies { get; set; } = [];
}
