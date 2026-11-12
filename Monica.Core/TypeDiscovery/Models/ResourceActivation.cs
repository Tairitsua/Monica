using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.Core.TypeDiscovery.Models;

/// <summary>An immutable activation observation for one native resource, without constructing its implementation.</summary>
public sealed record ResourceActivation
{
    /// <summary>Gets the stable, module-prefixed resource key within this host.</summary>
    public required string Key { get; init; }
    /// <summary>Gets the actual implementation type, or null for an opaque service factory.</summary>
    public Type? ImplementationType { get; init; }
    /// <summary>Gets the capability that owns admission and any manual operation.</summary>
    public required TypeActivationKind Kind { get; init; }
    /// <summary>Gets the declaring owner or resource anchor, or null for unowned infrastructure.</summary>
    public Type? RegistrationOwner { get; init; }
    /// <summary>Gets the effective local activation decision.</summary>
    public required ActivationMode Mode { get; init; }
    /// <summary>Gets a safe explanation authored by the module; configuration values and exception payloads must be excluded.</summary>
    public required string Reason { get; init; }
}
