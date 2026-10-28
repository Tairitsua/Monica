namespace Monica.EntityFrameworkCore.Models;

/// <summary>Explicit capabilities for a relational provider that Monica does not recognize.</summary>
/// <remarks>Describe namespaces within one physical database. MySQL's database synonym is not an independent schema.</remarks>
public sealed record InfrastructureProviderCapabilities
{
    /// <summary>Creates a capability declaration; the identifier limit is measured conservatively in UTF-8 bytes.</summary>
    public InfrastructureProviderCapabilities(bool supportsSchemas, int maxIdentifierLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxIdentifierLength, 1);
        SupportsSchemas = supportsSchemas;
        MaxIdentifierLength = maxIdentifierLength;
    }

    /// <summary>Whether a schema can be selected without changing physical databases.</summary>
    public bool SupportsSchemas { get; }

    /// <summary>Maximum supported identifier length. Identifiers are rejected rather than truncated.</summary>
    public int MaxIdentifierLength { get; }
}
