using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Monica.Core.Configuration;

/// <summary>A read-only copy of one provider's contribution, shapes and original source identity.</summary>
public sealed class ConfigurationProviderSnapshot : IConfigurationProvider, IConfigurationValueShapeProvider
{
    private readonly IDictionary<string, string?> _values;
    private readonly ConfigurationChildKeyIndex _children = new();

    internal ConfigurationProviderSnapshot(ConfigurationProviderOrigin origin, ConfigurationValueProjection projection)
    {
        Origin = origin;
        Values = projection.Values;
        Shapes = projection.Shapes;
        _values = new Dictionary<string, string?>(Values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the original source identity captured together with this contribution.</summary>
    public ConfigurationProviderOrigin Origin { get; }

    /// <summary>Gets explicit values; omission remains distinct from explicit null.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>Gets the preserved scalar and container shapes.</summary>
    public IReadOnlyDictionary<string, ConfigurationValueShape> Shapes { get; }

    /// <summary>Reads a section from this provider alone, without lower-priority fallback.</summary>
    public IConfigurationSection GetSection(string path) => new ConfigurationSnapshot([this]).GetSection(path);

    /// <summary>Returns whether this provider explicitly supplies the path or one of its descendants.</summary>
    public bool Contains(string path) => Values.ContainsKey(path) || Shapes.ContainsKey(path)
        || GetChildKeys([], path.Length == 0 ? null : path).Any();

    /// <inheritdoc />
    public bool TryGet(string key, out string? value) => Values.TryGetValue(key, out value);

    /// <inheritdoc />
    public bool TryGetShape(string path, out ConfigurationValueShape shape) => Shapes.TryGetValue(path, out shape);

    /// <inheritdoc />
    public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        => _children.GetChildKeys(_values, earlierKeys, parentPath);

    /// <summary>Always throws because a captured contribution is immutable.</summary>
    public void Set(string key, string? value) => throw new NotSupportedException("A configuration snapshot is read-only.");

    /// <summary>Does nothing; loading a snapshot never reads its original provider again.</summary>
    public void Load() { }

    /// <inheritdoc />
    public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
}
