namespace Monica.Application.Annotations;

/// <summary>Defines shared HTTP wire conventions without publishing receivers or selecting caller transports.</summary>
/// <param name="routePrefix">The relative route prefix, defaulting to api/v1. Clients and receivers read it from the request assembly.</param>
/// <remarks>Use on the contract assembly when overriding the default prefix. Host-owned WebApiGenerationConfig controls publication separately.</remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class HttpApiContractAttribute(string routePrefix = "api/v1") : Attribute
{
    /// <summary>Gets the shared route prefix. Leading/trailing slashes and route placeholders are not supported.</summary>
    public string RoutePrefix { get; } = routePrefix;

    /// <summary>Names the domain for service-private requests outside PublishedLanguages. Published namespace domains remain authoritative.</summary>
    public string? DomainName { get; set; }
}
