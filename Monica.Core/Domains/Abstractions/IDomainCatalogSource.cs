namespace Monica.Core.Domains.Abstractions;

/// <summary>Contributes startup domain identities from an infrastructure-owned inventory.</summary>
/// <remarks>Applications configure labels through AddDomainCatalog; they do not need to implement this integration contract.
/// Sources must return stable identities for the host lifetime and must not depend on IDomainCatalog.</remarks>
public interface IDomainCatalogSource
{
    /// <summary>Returns known domain identifiers independently of whether an instance is online.</summary>
    IEnumerable<string> GetDomainIds();
}
