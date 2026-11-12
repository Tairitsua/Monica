using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Primitives;

namespace Monica.Core.Configuration;

/// <summary>A frozen configuration root retaining ordered provider contributions and source shapes.</summary>
/// <remarks>Capturing does not resolve services, reread files or dispose the original providers.</remarks>
public sealed class ConfigurationSnapshot : IConfigurationRoot
{
    private readonly ConfigurationShapeView _view;

    internal ConfigurationSnapshot(IReadOnlyList<ConfigurationProviderSnapshot> contributions)
    {
        Contributions = Array.AsReadOnly(contributions.ToArray());
        _view = new ConfigurationShapeView(this);
    }

    /// <summary>Gets original contributions in increasing priority order.</summary>
    public IReadOnlyList<ConfigurationProviderSnapshot> Contributions { get; }

    /// <inheritdoc />
    public IEnumerable<IConfigurationProvider> Providers => Contributions;

    /// <summary>Copies all leaf providers, expanding chained roots without losing their origin paths.</summary>
    /// <exception cref="InvalidOperationException">The provider chain is cyclic or exceeds the supported depth.</exception>
    public static ConfigurationSnapshot Capture(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var result = new List<ConfigurationProviderSnapshot>();
        CaptureRoot(configuration, string.Empty, new HashSet<IConfiguration>(ReferenceEqualityComparer.Instance), result);
        return new ConfigurationSnapshot(result);
    }

    /// <summary>Selects the highest provider explicitly supplying a complete structural value.</summary>
    /// <remarks>
    /// The returned contribution is read on its own. Explicit null, empty containers and ancestor barriers are
    /// selections, never requests to fall back. Ordinary reads on this root retain normal indexed-key overlays.
    /// </remarks>
    public ConfigurationProviderSnapshot? SelectCompleteSection(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Contributions.LastOrDefault(provider => provider.Contains(path) || ConfigurationShapeView.HasBarrier(provider, path));
    }

    /// <inheritdoc />
    public string? this[string key]
    {
        get => _view.GetSection(key).Value;
        set => throw new NotSupportedException("A configuration snapshot is read-only.");
    }

    /// <inheritdoc />
    public IConfigurationSection GetSection(string key) => _view.GetSection(key);

    /// <inheritdoc />
    public IEnumerable<IConfigurationSection> GetChildren() => GetSection(string.Empty).GetChildren();

    /// <inheritdoc />
    public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

    /// <summary>Always throws because the captured inputs cannot be replaced by a reload.</summary>
    public void Reload() => throw new NotSupportedException("A configuration snapshot cannot be reloaded.");

    private static void CaptureRoot(IConfigurationRoot root, string prefix, ISet<IConfiguration> visited,
        List<ConfigurationProviderSnapshot> result)
    {
        if (!visited.Add(root)) throw new InvalidOperationException("Configuration provider chains must be acyclic.");
        if (visited.Count > 256) throw new InvalidOperationException("Configuration provider chains exceed the supported depth.");
        foreach (var (provider, index) in root.Providers.Select((provider, index) => (provider, index)))
        {
            var providerPath = prefix.Length == 0 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{prefix}/{index}";
            if (provider is ChainedConfigurationProvider { Configuration: IConfigurationRoot chained })
            {
                CaptureRoot(chained, providerPath, visited, result);
                continue;
            }

            var origin = provider is ConfigurationProviderSnapshot existing
                ? existing.Origin with { PriorityIndex = result.Count, ProviderPath = providerPath }
                : GetOrigin(provider, result.Count, providerPath);
            result.Add(new ConfigurationProviderSnapshot(origin, CaptureProjection(provider)));
        }
        visited.Remove(root);
    }

    internal static ConfigurationProviderOrigin GetOrigin(IConfigurationProvider provider, int priority, string providerPath)
    {
        var file = (provider as FileConfigurationProvider)?.Source;
        string? physicalPath = null;
        try
        {
            physicalPath = string.IsNullOrEmpty(file?.Path) ? null : file.FileProvider?.GetFileInfo(file.Path).PhysicalPath;
            if (physicalPath is not null) physicalPath = Path.GetFullPath(physicalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Source identity remains useful when a custom file provider has no resolvable physical path.
        }
        return new ConfigurationProviderOrigin
        {
            ProviderType = provider.GetType(), PriorityIndex = priority, ProviderPath = providerPath,
            SourcePath = file?.Path, PhysicalPath = physicalPath,
            Optional = file?.Optional, ReloadOnChange = file?.ReloadOnChange
        };
    }

    internal static ConfigurationValueProjection CaptureProjection(IConfigurationProvider provider)
    {
        if (provider is IConfigurationValueSnapshotProvider atomic) return atomic.CaptureProjection();
        if (provider is ConfigurationProviderSnapshot snapshot) return new(snapshot.Values, snapshot.Shapes);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var shapes = new Dictionary<string, ConfigurationValueShape>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((string.Empty, 0));
        while (pending.TryDequeue(out var item))
        {
            if (!visited.Add(item.Path)) continue;
            if (item.Depth > 256) throw new InvalidOperationException("Configuration provider contribution exceeds the supported depth.");
            var hasValue = provider.TryGet(item.Path, out var value);
            if (hasValue) values[item.Path] = value;
            var children = provider.GetChildKeys([], item.Path.Length == 0 ? null : item.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (provider is IConfigurationValueShapeProvider shapeProvider && shapeProvider.TryGetShape(item.Path, out var shape))
                shapes[item.Path] = shape;
            else if (hasValue)
                shapes[item.Path] = value is not null ? ConfigurationValueShape.Scalar
                    : provider is JsonConfigurationProvider ? ConfigurationValueShape.AmbiguousNull : ConfigurationValueShape.Null;
            else if (children.Length > 0) shapes[item.Path] = ConfigurationValueShape.Container;
            foreach (var key in children)
                pending.Enqueue((item.Path.Length == 0 ? key : $"{item.Path}:{key}", item.Depth + 1));
        }
        return new(values, shapes);
    }
}
