using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Monica.Configuration.Binding;

/// <summary>Preserves container and explicit-null identities lost by flat configuration values.</summary>
internal enum ConfigurationValueShape { Scalar, Null, Object, List, Container, AmbiguousNull }

internal sealed record ConfigurationValueProjection(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyDictionary<string, ConfigurationValueShape> Shapes);

internal interface IConfigurationValueShapeProvider
{
    bool TryGetShape(string path, out ConfigurationValueShape shape);
}

/// <summary>Projects one JSON document without serializing CLR objects or discarding empty containers.</summary>
internal static class ConfigurationValueProjectionFactory
{
    internal static ConfigurationValueProjection Create(string path, JsonElement value)
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

    internal static ConfigurationValueProjection CreateDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON configuration document must have an object root.");
        return Create(string.Empty, document);
    }

    internal static ConfigurationValueProjection Set(IReadOnlyDictionary<string, string?> values,
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
        shapes[path] = value.ValueKind switch
        {
            JsonValueKind.Object => ConfigurationValueShape.Object,
            JsonValueKind.Array => ConfigurationValueShape.List,
            JsonValueKind.Null => ConfigurationValueShape.Null,
            _ => ConfigurationValueShape.Scalar
        };
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
internal sealed class ConfigurationValueProjectionSource(ConfigurationValueProjection projection) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new ConfigurationValueProjectionProvider(projection);
}

internal sealed class ConfigurationValueProjectionProvider(ConfigurationValueProjection projection)
    : ConfigurationProvider, IConfigurationValueShapeProvider
{
    private IReadOnlyDictionary<string, ConfigurationValueShape> _shapes = projection.Shapes;
    public override void Load()
    {
        _shapes = projection.Shapes;
        Data = new Dictionary<string, string?>(projection.Values, StringComparer.OrdinalIgnoreCase);
    }
    public bool TryGetShape(string path, out ConfigurationValueShape shape) => _shapes.TryGetValue(path, out shape);
    public override void Set(string key, string? value)
    {
        var next = ConfigurationValueProjectionFactory.Set(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), _shapes, key, value);
        _shapes = next.Shapes;
        Data = new Dictionary<string, string?>(next.Values, StringComparer.OrdinalIgnoreCase);
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
    : JsonConfigurationProvider(source), IConfigurationValueShapeProvider
{
    private IReadOnlyDictionary<string, ConfigurationValueShape> _shapes = new Dictionary<string, ConfigurationValueShape>();

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
        base.Load(buffer);
        _shapes = shapes;
    }

    internal void RestoreLoadedContribution(IReadOnlyDictionary<string, string?> values)
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

    public bool TryGetShape(string path, out ConfigurationValueShape shape)
    {
        return _shapes.TryGetValue(path, out shape)
            && (TryGet(path, out _) || GetChildKeys([], path.Length == 0 ? null : path).Any());
    }

    public override void Set(string key, string? value)
    {
        var next = ConfigurationValueProjectionFactory.Set(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), _shapes, key, value);
        _shapes = next.Shapes;
        Data = new Dictionary<string, string?>(next.Values, StringComparer.OrdinalIgnoreCase);
    }
}

internal static class ShapeAwareJsonConfigurationExtensions
{
    internal static void PreserveJsonShapes(IConfigurationBuilder builder)
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

    internal static IConfigurationBuilder AddShapeAwareJsonFile(this IConfigurationBuilder builder, string path,
        bool optional, bool reloadOnChange)
    {
        var source = new ShapeAwareJsonConfigurationSource { Path = path, Optional = optional, ReloadOnChange = reloadOnChange };
        source.ResolveFileProvider();
        return builder.Add(source);
    }
}
