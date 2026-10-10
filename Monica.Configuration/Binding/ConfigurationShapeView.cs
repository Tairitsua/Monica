using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Monica.Configuration.Binding;

/// <summary>
/// Reads provider precedence while retaining indexed-key overlay semantics. Only explicit null and empty containers
/// block lower descendants; nonempty collections continue to merge by Microsoft configuration keys.
/// </summary>
internal sealed class ConfigurationShapeView(IConfiguration configuration)
{
    private readonly IConfigurationProvider[]? _providers = configuration is IConfigurationRoot root
        ? ExpandProviders(root, new HashSet<IConfiguration>(ReferenceEqualityComparer.Instance)).ToArray() : null;

    private static IEnumerable<IConfigurationProvider> ExpandProviders(IConfigurationRoot root, ISet<IConfiguration> visited)
    {
        if (!visited.Add(root)) throw new InvalidOperationException("Configuration provider chains must be acyclic.");
        foreach (var provider in root.Providers)
        {
            if (provider is ChainedConfigurationProvider { Configuration: IConfigurationRoot chained })
            {
                foreach (var child in ExpandProviders(chained, visited)) yield return child;
            }
            else yield return provider;
        }
        visited.Remove(root);
    }

    internal IConfigurationSection GetSection(string path) => new ShapeSection(this, path);

    internal bool TryGetShape(string path, out ConfigurationValueShape shape)
    {
        if (_providers is not null)
        {
            for (var i = _providers.Length - 1; i >= 0; i--)
            {
                var provider = _providers[i];
                if (HasBarrier(provider, path)) break;
                if (provider is IConfigurationValueShapeProvider shapes && shapes.TryGetShape(path, out shape)) return true;
                if (provider.TryGet(path, out var value))
                {
                    shape = value is null
                        ? provider.GetType().Namespace == "Microsoft.Extensions.Configuration.Json"
                            ? ConfigurationValueShape.AmbiguousNull : ConfigurationValueShape.Null
                        : ConfigurationValueShape.Scalar;
                    return true;
                }
                if (provider.GetChildKeys([], path.Length == 0 ? null : path).Any())
                {
                    shape = ConfigurationValueShape.Container;
                    return true;
                }
            }
        }

        if (GetChildren(path).Any())
        {
            shape = ConfigurationValueShape.Container;
            return true;
        }
        if (GetValue(path) is not null)
        {
            shape = ConfigurationValueShape.Scalar;
            return true;
        }
        shape = default;
        return false;
    }

    private string? GetValue(string path)
    {
        if (_providers is null) return configuration[path];
        for (var i = _providers.Length - 1; i >= 0; i--)
        {
            if (HasBarrier(_providers[i], path)) return null;
            if (_providers[i].TryGet(path, out var value)) return value;
        }
        return null;
    }

    private IEnumerable<IConfigurationSection> GetChildren(string path)
    {
        if (_providers is null) return configuration.GetSection(path).GetChildren();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _providers)
        {
            if (HasBarrier(provider, path, includeSelf: true)) keys.Clear();
            if (HasBarrier(provider, path)
                || provider is IConfigurationValueShapeProvider shapes && shapes.TryGetShape(path, out var shape)
                && shape == ConfigurationValueShape.Null) continue;
            foreach (var key in provider.GetChildKeys([], path.Length == 0 ? null : path)) keys.Add(key);
        }
        return keys.Order(ConfigurationKeyComparer.Instance).Select(key => GetSection(path.Length == 0 ? key : $"{path}:{key}"));
    }

    private static bool HasBarrier(IConfigurationProvider provider, string path, bool includeSelf = false)
    {
        if (provider is not IConfigurationValueShapeProvider shapes) return false;
        var current = includeSelf ? path : Parent(path);
        while (current is not null)
        {
            if (shapes.TryGetShape(current, out var shape)
                && (shape == ConfigurationValueShape.Null
                    || shape is ConfigurationValueShape.List or ConfigurationValueShape.Object
                    && !provider.GetChildKeys([], current.Length == 0 ? null : current).Any())) return true;
            current = Parent(current);
        }
        return false;
    }

    private static string? Parent(string path)
    {
        if (path.Length == 0) return null;
        var separator = path.LastIndexOf(':');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private IChangeToken GetReloadToken() => configuration.GetReloadToken();

    private sealed class ShapeSection(ConfigurationShapeView owner, string path) : IConfigurationSection
    {
        public string Key => path[(path.LastIndexOf(':') + 1)..];
        public string Path => path;
        public string? Value { get => owner.GetValue(path); set => throw new NotSupportedException(); }
        public string? this[string key] { get => owner.GetValue(path.Length == 0 ? key : $"{path}:{key}"); set => throw new NotSupportedException(); }
        public IConfigurationSection GetSection(string key) => owner.GetSection(path.Length == 0 ? key : $"{path}:{key}");
        public IEnumerable<IConfigurationSection> GetChildren() => owner.GetChildren(path);
        public IChangeToken GetReloadToken() => owner.GetReloadToken();
    }
}
