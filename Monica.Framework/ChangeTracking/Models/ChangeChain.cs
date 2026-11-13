using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monica.Framework.ChangeTracking.Abstractions;
using Monica.Tool.Extensions;

namespace Monica.Framework.ChangeTracking.Models;

/// <summary>Replays accepted changes without persistence or external effects.</summary>
/// <remarks>Entry identities must be unique. Rollback targets must exist in this chain and form an acyclic graph.</remarks>
public class ChangeChain<TTargetEntity, TEntry, TChangeData, TSource>
    where TEntry : ChangeEntry<TTargetEntity, TChangeData, TSource>, new()
    where TChangeData : class, IChangeData<TTargetEntity>, new()
    where TSource : Enum
    where TTargetEntity : class, IChangeTrackedEntity
{
    /// <summary>The portable baseline and accepted facts, including frozen legacy tie ordering.</summary>
    public class ChainJsonParseBridge
    {
        /// <summary>Version 1 is the legacy document without an explicit ordering contract.</summary>
        public int SchemaVersion { get; set; } = 1;
        public required string TracingData { get; set; }
        public required List<TEntry> ChangingList { get; set; } = [];
    }

    private readonly SortedList<ChangeOrderKey, TEntry> _entries = [];
    private readonly Dictionary<string, TEntry> _byId = new(StringComparer.Ordinal);

    public ChangeChain(ChainJsonParseBridge bridge) : this(bridge.TracingData, ReadEntries(bridge)) { }
    public ChangeChain(string tracingData, List<TEntry> changingList)
    {
        TracingData = tracingData;
        InitChangingChain(changingList);
    }
    public ChangeChain(TTargetEntity entity) : this(entity.GetCurTracingData(), []) { }

    /// <summary>Current portable format; version 1 is accepted only through the bridge constructor.</summary>
    public int SchemaVersion => 2;

    [JsonIgnore]
    public DateTime? LastModifiedTime => _entries.Count == 0 ? null : _entries.Values[^1].OrderTime;

    [JsonIgnore]
    public ChangeOrderKey? LastOrderKey => _entries.Count == 0 ? null : _entries.Keys[^1];

    /// <summary>The original baseline. Appending changes never replaces it with the current projection.</summary>
    [JsonInclude]
    public string TracingData { get; init; }

    public IReadOnlyList<TEntry> ChangingList => GetChangingListSnapshot();
    public bool ContainsChangingItemId(string id) => _byId.ContainsKey(id);
    public List<TEntry> GetChangingListSnapshot() => _entries.Values.ToList();

    public IEnumerable<PropertyHistory<TEntry>> GetSingleAlterItemHistory<TValue>(Expression<Func<TChangeData, TValue>> expression)
        => GetSingleAlterItemHistory<TValue, bool>(expression, null);

    public IEnumerable<PropertyHistory<TEntry>> GetSingleAlterItemHistory<TValue, TNested>(
        Expression<Func<TChangeData, TValue>> expression,
        Expression<Func<TTargetEntity, TNested>>? nestedSelectExpression)
    {
        var state = GetInitialStatus();
        var currentValue = GetNestedValue(state)?.GetPropertyValue(expression.GetPropertyInfo().Name);
        yield return new PropertyHistory<TEntry>(null, currentValue, null);
        foreach (var entry in _entries.Values.Where(entry => !entry.IsInvalid && !entry.IsRollback
                     && expression.GetPropertyValue(entry.Data) != null))
        {
            var nextValue = GetNestedValue(entry.ApplyAlter(state))?.GetPropertyValue(expression.GetPropertyInfo().Name);
            yield return new PropertyHistory<TEntry>(currentValue, nextValue, entry);
            currentValue = nextValue;
        }

        object? GetNestedValue(TTargetEntity value) => nestedSelectExpression is null
            ? value : value.GetPropertyValue(nestedSelectExpression.GetPropertyInfo().Name);
    }

    /// <summary>Replaces the chain with accepted facts and rebuilds derived rollback state.</summary>
    public void InitChangingChain(List<TEntry> entries)
    {
        var previous = _entries.Values.ToArray();
        _entries.Clear();
        _byId.Clear();
        try
        {
            foreach (var entry in entries) Insert(entry);
            RebuildRollbacks();
        }
        catch
        {
            _entries.Clear();
            _byId.Clear();
            foreach (var entry in previous) Insert(entry);
            RebuildRollbacks();
            throw;
        }
    }

    /// <summary>Adds one accepted fact. Arrival order never adjusts its effective time.</summary>
    public void Add(TEntry entry)
    {
        Insert(entry);
        try { RebuildRollbacks(); }
        catch
        {
            _entries.Remove(entry.OrderKey);
            _byId.Remove(entry.Id);
            RebuildRollbacks();
            throw;
        }
    }

    public TTargetEntity GetInitialStatus()
    {
        try
        {
            return JsonSerializer.Deserialize<TTargetEntity>(TracingData, ChangeTrackingJsonPolicy.TracingDataOptions)
                ?? throw new JsonException("The baseline is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The change-chain baseline is invalid.", exception);
        }
    }

    public TTargetEntity GetFinalStatus()
    {
        var state = GetInitialStatus();
        foreach (var entry in _entries.Values) entry.ApplyAlter(state);
        return state;
    }

    public bool ShouldReconstruct(TEntry entry)
        => entry.IsRollback || LastOrderKey is { } last && entry.OrderKey.CompareTo(last) <= 0;

    public bool IsConsistent(TTargetEntity entity, [NotNullWhen(false)] out TChangeData? data)
    {
        data = entity.Equals(GetFinalStatus()) ? null : new TChangeData();
        return data is null;
    }

    public void PatchChain() { }

    /// <summary>Freezes the currently known direct and related source targets before the supplied operation time.</summary>
    /// <remarks>When omitted, the operation time defaults to the current local time. Retry owners should supply their original time.</remarks>
    public TEntry? CreateRollbackAlterItemBySourceId(string id, TSource source, string sourceId,
        string? sourceInfo, string targetSourceId, DateTime? operationTime = null)
    {
        var now = operationTime ?? DateTime.Now;
        var targets = _entries.Values.Where(entry =>
                (entry.SourceId == targetSourceId || entry.RelatedSourceIds?.Contains(targetSourceId) == true)
                && entry.AlterTime < now)
            .Select(entry => entry.Id).ToList();
        return targets.Count == 0 ? null : new TEntry
        {
            Id = id, Source = source, SourceId = sourceId, SourceInfo = sourceInfo,
            AlterTime = now, TargetRollbackIds = targets, Data = new TChangeData()
        };
    }

    private static List<TEntry> ReadEntries(ChainJsonParseBridge bridge)
    {
        if (bridge.SchemaVersion is not (1 or 2))
            throw new InvalidOperationException($"Unsupported change-chain format {bridge.SchemaVersion}.");
        if (bridge.SchemaVersion == 1)
            for (var index = 0; index < bridge.ChangingList.Count; index++)
                bridge.ChangingList[index].HistoricalOrder ??= index + 1L;
        return bridge.ChangingList;
    }

    private void Insert(TEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Id);
        if (!_byId.TryAdd(entry.Id, entry))
            throw new InvalidOperationException($"Duplicate change identity '{entry.Id}'.");
        _entries.Add(entry.OrderKey, entry);
    }

    private void RebuildRollbacks()
    {
        // Evaluate rollback dependencies before targets, independently of their business timestamps.
        // This also handles overlapping rollbacks and rollback-of-rollback without recursive traversal.
        var incoming = _byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var edges = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in _entries.Values.Where(entry => entry.IsRollback))
        {
            var targets = entry.TargetRollbackIds!.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var target in targets)
            {
                if (!incoming.ContainsKey(target))
                    throw new InvalidOperationException($"Rollback '{entry.Id}' references missing change '{target}'.");
                incoming[target]++;
            }
            edges.Add(entry.Id, targets);
        }
        var queue = new Queue<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var invalidatedBy = new Dictionary<string, TEntry>(StringComparer.Ordinal);
        var processed = 0;
        while (queue.TryDequeue(out var id))
        {
            processed++;
            if (!edges.TryGetValue(id, out var targets)) continue;
            var active = !invalidatedBy.ContainsKey(id);
            var rollback = _byId[id];
            foreach (var target in targets)
            {
                if (active && (!invalidatedBy.TryGetValue(target, out var previous)
                               || rollback.OrderKey.CompareTo(previous.OrderKey) > 0))
                    invalidatedBy[target] = rollback;
                if (--incoming[target] == 0) queue.Enqueue(target);
            }
        }
        if (processed != _byId.Count)
            throw new InvalidOperationException("Rollback targets form a cycle.");
        foreach (var entry in _entries.Values)
            entry.RollbackBy = invalidatedBy.TryGetValue(entry.Id, out var rollback) ? rollback.Id : null;
    }
}
