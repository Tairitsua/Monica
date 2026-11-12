using Monica.Core.Domains.Models;

namespace Monica.Core.Domains.Abstractions;

/// <summary>Host-owned, immutable business-domain metadata. It contains no service registrations or deployment decisions.</summary>
public interface IDomainCatalog
{
    /// <summary>Gets configured domains and identities contributed by compiled application ownership.</summary>
    IReadOnlyDictionary<string, DomainInfo> Domains { get; }

    /// <summary>Returns a known label or the original identifier for unlisted domains, without adding a catalog entry.</summary>
    string GetDisplayName(string domainId);
}
