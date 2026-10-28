using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Locker.Leases;
using Monica.Locker.Leases.Diagnostics;
using Monica.Modules;

namespace Monica.Locker.Services;

/// <summary>Owns per-authority inventory coalescing; exact reads remain separate bounded key lookups.</summary>
internal sealed class LeaseInventoryReader
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<LeaseInventoryReader> _logger;
    private readonly ModuleLeaseDiagnosticsOption _options;
    private readonly Dictionary<string, Entry> _entries;

    public LeaseInventoryReader(IServiceProvider services, TimeProvider time,
        IOptions<ModuleLeaseDiagnosticsOption> options, ILogger<LeaseInventoryReader> logger)
    {
        _services = services; _time = time; _logger = logger; _options = options.Value;
        if (_options.InventoryCacheDuration <= TimeSpan.Zero || _options.QueryTimeout <= TimeSpan.Zero
            || _options.MaxRecordsPerAuthority is < 1 or > 10000)
            throw new ArgumentException("Lease diagnostic cache duration, query timeout, or record limit is invalid.");
        _entries = _options.Authorities.ToDictionary(source => source.Id, source => new Entry(source), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<LeaseAuthorityEvidence>> ReadAsync(CancellationToken token)
    {
        var result = new List<LeaseAuthorityEvidence>(_entries.Count);
        // Each binding retains its own source clock and failure. Do not amplify provider pressure with unbounded fan-out.
        foreach (var entry in _entries.Values) result.Add(await ReadAsync(entry, token));
        return result;
    }

    private async Task<LeaseAuthorityEvidence> ReadAsync(Entry entry, CancellationToken token)
    {
        await entry.Gate.WaitAsync(token);
        try
        {
            if (entry.Evidence is not null && _time.GetElapsedTime(entry.AttemptedAt) < _options.InventoryCacheDuration)
                return entry.Evidence;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.QueryTimeout);
            try
            {
                var store = Resolve(entry.Authority);
                var snapshot = await store.GetSnapshotAsync(entry.Authority.Scope, entry.Authority.NamePrefix, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                var permitted = snapshot.Leases.Where(item => entry.Authority.Includes(item.Resource))
                    .DistinctBy(item => item.Resource).OrderBy(item => item.Resource.Name, StringComparer.Ordinal).ToArray();
                entry.Evidence = new(entry.Authority,
                    new(snapshot.ObservedAtUtc, permitted.Take(_options.MaxRecordsPerAuthority).ToArray()), false,
                    entry.Authority.AllowRevocation && store is ILeaseAdministration, permitted.Length);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Lease inventory read failed for authority {AuthorityId}", entry.Authority.Id);
                entry.Evidence = new(entry.Authority, entry.Evidence?.Snapshot, true,
                    entry.Evidence?.CanRevoke ?? false, entry.Evidence?.TotalCount ?? 0);
            }
            entry.AttemptedAt = _time.GetTimestamp();
            return entry.Evidence;
        }
        finally { entry.Gate.Release(); }
    }

    public async Task<LeaseResourceEvidence> ReadResourceAsync(string authorityId, LeaseResource resource, CancellationToken token)
    {
        var entry = Find(authorityId, resource);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.QueryTimeout);
        var store = Resolve(entry.Authority);
        var snapshot = await store.GetSnapshotAsync(resource, timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        return new(entry.Authority, new(snapshot.ObservedAtUtc,
            snapshot.Leases.Where(item => item.Resource == resource).Take(1).ToArray()),
            entry.Authority.AllowRevocation && store is ILeaseAdministration);
    }

    public async Task<bool> RevokeAsync(string authorityId, LeaseResource resource, CancellationToken token)
    {
        var entry = Find(authorityId, resource);
        if (!entry.Authority.AllowRevocation || Resolve(entry.Authority) is not ILeaseAdministration admin)
            throw new UnauthorizedAccessException("This authority does not permit lease revocation.");
        // Serialize against inventory adoption so a pre-revocation scan cannot repopulate the cache afterward.
        await entry.Gate.WaitAsync(token);
        try
        {
            var revoked = await admin.RevokeAsync(resource, token);
            entry.Evidence = null;
            _logger.LogWarning("Operator revoked current lease for authority {AuthorityId}, scope {Scope}, resource {Resource}; removed: {Removed}",
                authorityId, resource.Scope, resource.Name, revoked);
            return revoked;
        }
        finally { entry.Gate.Release(); }
    }

    private Entry Find(string authorityId, LeaseResource resource) =>
        _entries.TryGetValue(authorityId, out var entry) && entry.Authority.Includes(resource)
            ? entry : throw new ArgumentException("The resource is outside the configured authority query.");

    private IExclusiveLeaseStore Resolve(LeaseAuthority authority) =>
        _services.GetRequiredKeyedService<IExclusiveLeaseStore>(authority.StoreKey);

    private sealed class Entry(LeaseAuthority authority)
    {
        public LeaseAuthority Authority { get; } = authority;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public LeaseAuthorityEvidence? Evidence { get; set; }
        public long AttemptedAt { get; set; }
    }
}
