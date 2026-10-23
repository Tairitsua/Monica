using Microsoft.Extensions.Configuration;

namespace Monica.Configuration.Binding;

/// <summary>
/// Indexes immediate child keys for providers that replace their data dictionary on every write.
/// The dictionary identity invalidates the index even when Set does not raise a reload notification.
/// </summary>
internal sealed class ConfigurationChildKeyIndex
{
    private readonly Lock _gate = new();
    private Snapshot? _snapshot;

    internal IEnumerable<string> GetChildKeys(IDictionary<string, string?> data,
        IEnumerable<string> earlierKeys, string? parentPath)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || !ReferenceEquals(snapshot.Data, data))
        {
            lock (_gate)
            {
                snapshot = _snapshot;
                if (snapshot is null || !ReferenceEquals(snapshot.Data, data))
                {
                    snapshot = CreateSnapshot(data);
                    Volatile.Write(ref _snapshot, snapshot);
                }
            }
        }

        var children = parentPath is null ? snapshot.RootChildren
            : snapshot.Children.GetValueOrDefault(parentPath) ?? [];
        if (earlierKeys.TryGetNonEnumeratedCount(out var count) && count == 0) return children;

        // Preserve ConfigurationProvider's duplicate keys and numeric ordering across providers.
        var combined = new List<string>(children);
        combined.AddRange(earlierKeys);
        combined.Sort(ConfigurationKeyComparer.Instance);
        return combined;
    }

    private static Snapshot CreateSnapshot(IDictionary<string, string?> data)
    {
        var rootChildren = new List<string>();
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in data.Keys)
        {
            var separator = key.IndexOf(':');
            rootChildren.Add(separator < 0 ? key : key[..separator]);
            while (separator >= 0)
            {
                var parent = key[..separator];
                var nextSeparator = key.IndexOf(':', separator + 1);
                var child = nextSeparator < 0 ? key[(separator + 1)..] : key[(separator + 1)..nextSeparator];
                if (!children.TryGetValue(parent, out var keys)) children[parent] = keys = [];
                keys.Add(child);
                separator = nextSeparator;
            }
        }

        rootChildren.Sort(ConfigurationKeyComparer.Instance);
        var sorted = new Dictionary<string, IReadOnlyList<string>>(children.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (parent, keys) in children)
        {
            keys.Sort(ConfigurationKeyComparer.Instance);
            sorted[parent] = keys.AsReadOnly();
        }
        return new Snapshot(data, rootChildren.AsReadOnly(), sorted);
    }

    private sealed record Snapshot(IDictionary<string, string?> Data, IReadOnlyList<string> RootChildren,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Children);
}
