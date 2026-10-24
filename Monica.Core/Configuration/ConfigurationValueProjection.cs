using System.Text.Json;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Monica.Core.Configuration;

/// <summary>Preserves container and explicit-null identities lost by flat configuration values.</summary>
public enum ConfigurationValueShape
{
    /// <summary>A non-null scalar value.</summary>
    Scalar,
    /// <summary>An explicit null value.</summary>
    Null,
    /// <summary>A JSON object, including an empty object.</summary>
    Object,
    /// <summary>A JSON array, including an empty array.</summary>
    List,
    /// <summary>A container inferred from descendant configuration keys.</summary>
    Container,
    /// <summary>A legacy JSON null whose original null or empty-container shape is unavailable.</summary>
    AmbiguousNull
}

/// <summary>An immutable provider contribution, including shape information not represented by flat values.</summary>
public sealed class ConfigurationValueProjection
{
    /// <summary>Copies one provider's values and shapes using case-insensitive configuration paths.</summary>
    public ConfigurationValueProjection(IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, ConfigurationValueShape> shapes)
    {
        Values = new ReadOnlyDictionary<string, string?>(new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase));
        Shapes = new ReadOnlyDictionary<string, ConfigurationValueShape>(new Dictionary<string, ConfigurationValueShape>(shapes, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Gets explicit values; absent keys remain distinct from keys with a null value.</summary>
    public IReadOnlyDictionary<string, string?> Values { get; }

    /// <summary>Gets the original container and scalar shapes.</summary>
    public IReadOnlyDictionary<string, ConfigurationValueShape> Shapes { get; }
}

/// <summary>Supplies source shapes that the standard flat configuration contract cannot express.</summary>
public interface IConfigurationValueShapeProvider
{
    /// <summary>Returns whether the provider explicitly describes the given path's shape.</summary>
    bool TryGetShape(string path, out ConfigurationValueShape shape);
}

internal interface IConfigurationValueSnapshotProvider
{
    ConfigurationValueProjection CaptureProjection();
}

/// <summary>Projects one JSON document without serializing CLR objects or discarding empty containers.</summary>
public static class ConfigurationValueProjectionFactory
{
    /// <summary>Projects JSON at a configuration path, retaining null and empty containers.</summary>
    public static ConfigurationValueProjection Create(string path, JsonElement value)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var shapes = new Dictionary<string, ConfigurationValueShape>(StringComparer.OrdinalIgnoreCase);
        Flatten(path, value, values, shapes);
        // The JSON file parser does not create a configuration key for an empty top-level object.
        // A whole-file preview therefore cannot use that object as a barrier over lower providers.
        if (path.Length == 0 && value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any())
        {
            values.Remove(string.Empty);
            shapes.Remove(string.Empty);
        }
        return new(values, shapes);
    }

    /// <summary>Projects a JSON configuration document with an object root.</summary>
    public static ConfigurationValueProjection CreateDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON configuration document must have an object root.");
        return Create(string.Empty, document);
    }

    /// <summary>Replaces one value and its descendants while preserving the remaining provider contribution.</summary>
    public static ConfigurationValueProjection Set(IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, ConfigurationValueShape> shapes, string key, string? value)
    {
        var nextValues = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        var nextShapes = new Dictionary<string, ConfigurationValueShape>(shapes, StringComparer.OrdinalIgnoreCase);
        var prefix = key.Length == 0 ? string.Empty : $"{key}:";
        foreach (var child in nextValues.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            nextValues.Remove(child);
        foreach (var child in nextShapes.Keys.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
            nextShapes.Remove(child);
        nextValues[key] = value;
        nextShapes[key] = value is null ? ConfigurationValueShape.Null : ConfigurationValueShape.Scalar;
        var parent = key;
        while (parent.Length > 0)
        {
            var separator = parent.LastIndexOf(':');
            parent = separator < 0 ? string.Empty : parent[..separator];
            nextValues.Remove(parent);
            if (!nextShapes.TryGetValue(parent, out var parentShape)
                || parentShape is not (ConfigurationValueShape.Object or ConfigurationValueShape.List))
                nextShapes[parent] = ConfigurationValueShape.Container;
        }
        return new(nextValues, nextShapes);
    }

    private static void Flatten(string path, JsonElement value, IDictionary<string, string?> values,
        IDictionary<string, ConfigurationValueShape> shapes)
    {
        var shape = value.ValueKind switch
        {
            JsonValueKind.Object => ConfigurationValueShape.Object,
            JsonValueKind.Array => ConfigurationValueShape.List,
            JsonValueKind.Null => ConfigurationValueShape.Null,
            _ => ConfigurationValueShape.Scalar
        };
        if (!shapes.TryAdd(path, shape))
            throw new FormatException("A JSON configuration document contains duplicate flattened configuration keys.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject()) Flatten(Join(path, property.Name), property.Value, values, shapes);
                if (!value.EnumerateObject().Any()) AddValue(values, path, null);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray()) Flatten(Join(path, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)), item, values, shapes);
                if (index == 0) AddValue(values, path, string.Empty);
                break;
            case JsonValueKind.String: AddValue(values, path, value.GetString()); break;
            case JsonValueKind.Null: AddValue(values, path, null); break;
            default: AddValue(values, path, value.ToString()); break;
        }
    }

    private static void AddValue(IDictionary<string, string?> values, string path, string? value)
    {
        if (!values.TryAdd(path, value))
            throw new FormatException("A JSON configuration document contains duplicate flattened configuration keys.");
    }

    private static string Join(string path, string key) => string.IsNullOrEmpty(path) ? key : $"{path}:{key}";
}

/// <summary>Adds a shape-preserving projection at its declared provider priority.</summary>
public sealed class ConfigurationValueProjectionSource(ConfigurationValueProjection projection) : IConfigurationSource
{
    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new ConfigurationValueProjectionProvider(projection);
}

internal sealed class ConfigurationValueProjectionProvider(ConfigurationValueProjection projection)
    : ConfigurationProvider, IConfigurationValueShapeProvider, IConfigurationValueSnapshotProvider
{
    private readonly Lock _gate = new();
    private readonly ConfigurationChildKeyIndex _childKeys = new();
    private IReadOnlyDictionary<string, ConfigurationValueShape> _shapes = projection.Shapes;
    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        lock (_gate) return _childKeys.GetChildKeys(Data, earlierKeys, parentPath);
    }
    public override bool TryGet(string key, out string? value)
    {
        lock (_gate) return base.TryGet(key, out value);
    }
    public override void Load()
    {
        lock (_gate)
        {
            _shapes = projection.Shapes;
            Data = new Dictionary<string, string?>(projection.Values, StringComparer.OrdinalIgnoreCase);
        }
    }
    public bool TryGetShape(string path, out ConfigurationValueShape shape)
    {
        lock (_gate) return _shapes.TryGetValue(path, out shape);
    }
    public ConfigurationValueProjection CaptureProjection()
    {
        lock (_gate) return new(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), _shapes);
    }
    internal void RestoreProjection(ConfigurationValueProjection values)
    {
        lock (_gate)
        {
            _shapes = values.Shapes;
            Data = new Dictionary<string, string?>(values.Values, StringComparer.OrdinalIgnoreCase);
        }
    }
    public override void Set(string key, string? value)
    {
        lock (_gate)
        {
            var next = ConfigurationValueProjectionFactory.Set(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), _shapes, key, value);
            _shapes = next.Shapes;
            Data = new Dictionary<string, string?>(next.Values, StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Keeps the ordinary JSON file-provider lifecycle and its original source identity.</summary>
internal sealed class ShapeAwareJsonConfigurationSource : JsonConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ShapeAwareJsonConfigurationProvider(this);
    }
}

internal sealed class ShapeAwareJsonConfigurationProvider(JsonConfigurationSource source)
    : JsonConfigurationProvider(source), IConfigurationValueShapeProvider, IConfigurationValueSnapshotProvider
{
    private readonly Lock _gate = new();
    private readonly ConfigurationChildKeyIndex _childKeys = new();
    private IReadOnlyDictionary<string, ConfigurationValueShape> _shapes = new Dictionary<string, ConfigurationValueShape>();

    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        lock (_gate) return _childKeys.GetChildKeys(Data, earlierKeys, parentPath);
    }
    public override bool TryGet(string key, out string? value)
    {
        lock (_gate) return base.TryGet(key, out value);
    }

    public override void Load(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        var shapes = ConfigurationValueProjectionFactory.CreateDocument(document.RootElement).Shapes;
        // JsonConfigurationProvider owns a StreamReader that closes this buffer. Parse our metadata first.
        buffer.Position = 0;
        lock (_gate)
        {
            base.Load(buffer);
            _shapes = shapes;
        }
    }

    internal void RestoreLoadedContribution(IReadOnlyDictionary<string, string?> values)
    {
        lock (_gate)
        {
            var shapes = new Dictionary<string, ConfigurationValueShape>(_shapes, StringComparer.OrdinalIgnoreCase);
            foreach (var (path, value) in values)
            {
                if (!TryGet(path, out var loaded) || !string.Equals(value, loaded, StringComparison.Ordinal))
                    shapes[path] = value is null ? ConfigurationValueShape.Null : ConfigurationValueShape.Scalar;
            }
            _shapes = shapes;
            Data = new Dictionary<string, string?>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal void RestoreProjection(ConfigurationValueProjection values)
    {
        lock (_gate)
        {
            _shapes = values.Shapes;
            Data = new Dictionary<string, string?>(values.Values, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool TryGetShape(string path, out ConfigurationValueShape shape)
    {
        lock (_gate) return _shapes.TryGetValue(path, out shape)
            && (TryGet(path, out _) || GetChildKeys([], path.Length == 0 ? null : path).Any());
    }

    public ConfigurationValueProjection CaptureProjection()
    {
        lock (_gate)
        {
            var shapes = _shapes.Where(pair => TryGetShape(pair.Key, out _))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return new(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), shapes);
        }
    }

    public override void Set(string key, string? value)
    {
        lock (_gate)
        {
            var next = ConfigurationValueProjectionFactory.Set(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), _shapes, key, value);
            _shapes = next.Shapes;
            Data = new Dictionary<string, string?>(next.Values, StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Retains JSON shapes while preserving normal file-provider ordering and reload behavior.</summary>
public static class ShapeAwareJsonConfigurationExtensions
{
    /// <summary>Upgrades ordinary JSON sources without discarding their already loaded contributions.</summary>
    /// <remarks>Call before capturing startup configuration. Custom JSON-provider implementations remain unchanged.</remarks>
    public static void PreserveJsonShapes(IConfigurationBuilder builder)
    {
        var replace = Enumerable.Range(0, builder.Sources.Count)
            .Where(index => builder.Sources[index].GetType() == typeof(JsonConfigurationSource)).ToArray();
        if (replace.Length == 0) return;
        var contributions = builder is IConfigurationRoot currentRoot
            ? currentRoot.Providers.Select(CaptureContribution).ToArray() : null;
        foreach (var i in replace)
        {
            var source = (JsonConfigurationSource)builder.Sources[i];
            builder.Sources[i] = new ShapeAwareJsonConfigurationSource
            {
                Path = source.Path ?? string.Empty, FileProvider = source.FileProvider, Optional = source.Optional,
                ReloadOnChange = source.ReloadOnChange, ReloadDelay = source.ReloadDelay,
                OnLoadException = source.OnLoadException
            };
        }
        if (contributions is null || builder is not IConfigurationRoot rebuiltRoot) return;
        var providers = rebuiltRoot.Providers.ToArray();
        if (providers.Length != contributions.Length)
            throw new InvalidOperationException("Configuration provider topology changed while preserving JSON shapes.");
        for (var i = 0; i < providers.Length; i++)
        {
            if (providers[i] is ShapeAwareJsonConfigurationProvider json) json.RestoreLoadedContribution(contributions[i]);
            else foreach (var (key, value) in contributions[i])
                if (!providers[i].TryGet(key, out var loaded) || !string.Equals(value, loaded, StringComparison.Ordinal))
                    providers[i].Set(key, value);
        }
    }

    private static IReadOnlyDictionary<string, string?> CaptureContribution(IConfigurationProvider provider)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<(string Path, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((string.Empty, 0));
        while (pending.TryDequeue(out var item))
        {
            if (!visited.Add(item.Path)) continue;
            if (provider.TryGet(item.Path, out var value)) values[item.Path] = value;
            if (item.Depth > 256)
                throw new InvalidOperationException("Configuration provider contribution exceeds the supported projection depth.");
            foreach (var key in provider.GetChildKeys([], item.Path.Length == 0 ? null : item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                pending.Enqueue((item.Path.Length == 0 ? key : $"{item.Path}:{key}", item.Depth + 1));
        }
        return values;
    }

    /// <summary>Adds a JSON file whose explicit null and empty-container shapes survive configuration binding.</summary>
    public static IConfigurationBuilder AddShapeAwareJsonFile(this IConfigurationBuilder builder, string path,
        bool optional, bool reloadOnChange)
    {
        var source = new ShapeAwareJsonConfigurationSource { Path = path, Optional = optional, ReloadOnChange = reloadOnChange };
        source.ResolveFileProvider();
        return builder.Add(source);
    }
}
