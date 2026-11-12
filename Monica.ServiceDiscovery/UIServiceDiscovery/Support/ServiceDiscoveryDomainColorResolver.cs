using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.UIServiceDiscovery.Support;

public sealed class ServiceDiscoveryDomainColorResolver
{
    private static readonly string[] DomainRoles =
    [
        "primary",
        "secondary",
        "tertiary",
        "info",
        "success",
        "warning"
    ];

    private const string DefaultColorRole = "default";
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, string> _domainColors = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> AssignColors(IEnumerable<DomainInfo> domains)
    {
        var domainNames = domains
            .Select(domain => domain.Id)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        lock (_syncRoot)
        {
            foreach (var name in domainNames)
            {
                if (!_domainColors.ContainsKey(name))
                {
                    _domainColors[name] = DomainRoles[_domainColors.Count % DomainRoles.Length];
                }
            }

            return new Dictionary<string, string>(_domainColors, StringComparer.Ordinal);
        }
    }

    public string GetDomainColorRole(string? domainName)
    {
        if (string.IsNullOrWhiteSpace(domainName))
        {
            return DefaultColorRole;
        }

        lock (_syncRoot)
        {
            return _domainColors.GetValueOrDefault(domainName, DefaultColorRole);
        }
    }
}
