using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.Core.Modularity.Diagnostics.Models;

/// <summary>A portable resource activation observation containing safe names rather than runtime Type objects.</summary>
public sealed record ResourceActivationObservation
{
    /// <summary>Gets the module-prefixed identity within the serving host.</summary>
    public required string Key { get; init; }
    /// <summary>Gets the implementation's CLR name, or null for an opaque factory.</summary>
    public string? ImplementationTypeName { get; init; }
    /// <summary>Gets the declaring owner or resource anchor's CLR name, or null for unowned infrastructure.</summary>
    public string? RegistrationOwnerTypeName { get; init; }
    /// <summary>Gets the native capability responsible for admission.</summary>
    public required TypeActivationKind Kind { get; init; }
    /// <summary>Gets the current activation decision.</summary>
    public required ActivationMode Mode { get; init; }
    /// <summary>Gets the module-authored explanation without configuration or exception payloads.</summary>
    public required string Reason { get; init; }
}
