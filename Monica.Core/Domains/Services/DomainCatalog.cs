using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using Monica.Core.Domains.Abstractions;
using Monica.Core.Domains.Models;
using Monica.Modules;

namespace Monica.Core.Domains.Services;

internal sealed class DomainCatalog : IDomainCatalog
{
    public DomainCatalog(IOptions<ModuleDomainCatalogOption> options, IEnumerable<IDomainCatalogSource> sources)
    {
        var domains = new Dictionary<string, DomainInfo>(StringComparer.Ordinal);
        foreach (var domain in options.Value.Domains)
        {
            ValidateId(domain.Id);
            if (!domains.TryAdd(domain.Id, domain))
                throw new InvalidOperationException($"Domain '{domain.Id}' has multiple metadata declarations.");
        }
        foreach (var id in sources.SelectMany(source => source.GetDomainIds()))
        {
            ValidateId(id);
            domains.TryAdd(id, new DomainInfo { Id = id });
        }
        Domains = domains.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, DomainInfo> Domains { get; }

    public string GetDisplayName(string domainId) => Domains.TryGetValue(domainId, out var domain)
        && !string.IsNullOrWhiteSpace(domain.DisplayName) ? domain.DisplayName : domainId;

    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim())
            throw new InvalidOperationException("Domain identifiers must be nonempty and have no surrounding whitespace.");
    }
}
