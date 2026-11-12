using System.Reflection;

namespace Monica.Core.TypeDiscovery.Models;

/// <summary>
/// Configures how the domain type finder resolves assemblies to scan.
/// </summary>
/// <remarks>Host-owned options freeze before application registration; retained references reject later changes.</remarks>
public sealed class TypeFinderOptions
{
    private readonly HashSet<string> _addPatterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludePatterns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Assembly> _additionalAssemblies = [];
    private bool _frozen;

    /// <summary>
    /// Gets a value indicating whether project assemblies from <see cref="Microsoft.Extensions.DependencyModel.DependencyContext.Default"/>
    /// are included automatically.
    /// </summary>
    public bool UseDefaultProjectAssemblies { get; private set; } = true;

    /// <summary>
    /// Gets fuzzy include patterns that can add assemblies by name.
    /// </summary>
    public IReadOnlyCollection<string> AddPatterns => Array.AsReadOnly(_addPatterns.ToArray());

    /// <summary>
    /// Gets fuzzy exclude patterns that can remove assemblies by name.
    /// </summary>
    public IReadOnlyCollection<string> ExcludePatterns => Array.AsReadOnly(_excludePatterns.ToArray());

    /// <summary>
    /// Gets exact assembly instances that must always be considered during scanning.
    /// </summary>
    public IReadOnlyCollection<Assembly> AdditionalAssemblies => Array.AsReadOnly(_additionalAssemblies.ToArray());

    /// <summary>
    /// Adds fuzzy assembly-name patterns.
    /// </summary>
    public TypeFinderOptions Add(params string[] assemblyNamePatterns)
    {
        EnsureMutable();
        foreach (var pattern in assemblyNamePatterns.Where(static pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            _addPatterns.Add(pattern.Trim());
        }

        return this;
    }

    /// <summary>
    /// Adds exact assembly instances.
    /// </summary>
    public TypeFinderOptions Add(params Assembly[] assemblies)
    {
        EnsureMutable();
        foreach (var assembly in assemblies.Where(static assembly => assembly != null))
        {
            _additionalAssemblies.Add(assembly);
        }

        return this;
    }

    /// <summary>
    /// Adds fuzzy exclude patterns.
    /// </summary>
    public TypeFinderOptions Exclude(params string[] assemblyNamePatterns)
    {
        EnsureMutable();
        foreach (var pattern in assemblyNamePatterns.Where(static pattern => !string.IsNullOrWhiteSpace(pattern)))
        {
            _excludePatterns.Add(pattern.Trim());
        }

        return this;
    }

    /// <summary>
    /// Disables automatic inclusion of default project assemblies.
    /// </summary>
    public TypeFinderOptions ExcludeDefault()
    {
        EnsureMutable();
        UseDefaultProjectAssemblies = false;
        return this;
    }

    internal void Freeze() => _frozen = true;

    private void EnsureMutable()
    {
        if (_frozen) throw new InvalidOperationException("Type discovery is frozen before owned registration begins.");
    }
}
