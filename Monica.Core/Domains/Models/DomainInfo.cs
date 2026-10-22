namespace Monica.Core.Domains.Models;

/// <summary>Immutable descriptive metadata for a business domain, independent of deployment and runtime state.</summary>
public sealed record DomainInfo
{
    /// <summary>Gets the stable, case-sensitive identifier used by contracts and application ownership.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the optional human-readable label. Consumers fall back to <see cref="Id"/> when absent.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the optional description of the domain's responsibility.</summary>
    public string? Description { get; init; }
}
