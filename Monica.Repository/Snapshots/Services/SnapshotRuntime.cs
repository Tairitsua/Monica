using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.Snapshots.Metrics;
using System.Diagnostics;

namespace Monica.Repository.Snapshots.Services;

internal sealed class SnapshotSourceChangedException : InvalidOperationException
{
    public SnapshotSourceChangedException() : base("The snapshot source changed during validation or construction.") { }
}
internal sealed class SnapshotAuthorityException(string message) : InvalidOperationException(message);

/// <summary>Host-owned, bounded cache. Source authority and in-flight construction are independent of request lifetimes.</summary>
internal sealed class SnapshotRuntime<TContext>(SnapshotRegistration<TContext> registration,
    IDbContextFactory<TContext> factory, TimeProvider clock, IHostApplicationLifetime lifetime,
    ILogger<SnapshotRuntime<TContext>> logger, SnapshotMetrics metrics) : IAsyncDisposable where TContext : RepositoryDbContext<TContext>
{
    private readonly object _gate = new();
    private readonly Dictionary<ProjectionKey, Entry> _current = [];
    private readonly Dictionary<(ProjectionKey Key, SnapshotVersion Version), Task<Entry>> _builds = [];
    private readonly Dictionary<string, Observation> _observed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _parallelism = new(registration.MaximumConcurrentBuilds);
    private readonly CancellationTokenSource _stopping = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
    private long _sequence;
    private long _access;
    private bool _disposed;
    public SnapshotRegistration<TContext> Registration => registration;

    public async Task<SnapshotRead<T>> ReadAsync<T>(SnapshotDataset dataset, SnapshotProjectionIdentity identity,
        Func<TContext, CancellationToken, Task<T>> load, TimeSpan? maximumAge, CancellationToken token) where T : class
    {
        var key = new ProjectionKey(dataset.Key, identity);
        token.ThrowIfCancellationRequested();
        if (maximumAge is { } age && TryGetBounded(key, age, false, out var bounded))
        {
            metrics.Record("bounded_hit");
            return Result<T>(bounded!, identity, false, maximumAge);
        }
        try
        {
            for (var attempt = 0; attempt < registration.MaximumBuildAttempts; attempt++)
            {
                try
                {
                    var validation = await ValidateVersionAsync(dataset.Key, token);
                    lock (_gate)
                    {
                        ThrowIfStopped();
                        if (_current.TryGetValue(key, out var cached) && cached.Version == validation.Version
                            && IsObserved(validation))
                        {
                            var refreshed = validation.Timestamp > cached.Timestamp
                                ? cached with { Timestamp = validation.Timestamp, ValidatedAtUtc = validation.Utc, Access = ++_access }
                                : cached with { Access = ++_access };
                            _current[key] = refreshed;
                            metrics.Record("validated_hit");
                            return Result<T>(refreshed, identity, false, maximumAge);
                        }
                    }
                    var entry = await GetOrBuild(key, validation, async (db, ct) => (object)await load(db, ct))
                        .WaitAsync(token);
                    lock (_gate)
                    {
                        if (!_observed.TryGetValue(dataset.Key, out var observation) || observation.Version != entry.Version)
                            throw new SnapshotSourceChangedException();
                    }
                    return Result<T>(entry, identity, false, maximumAge);
                }
                catch (SnapshotSourceChangedException) when (attempt + 1 < registration.MaximumBuildAttempts) { metrics.Record("source_retry"); }
            }
            throw new SnapshotSourceChangedException();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SnapshotAuthorityException
            && maximumAge is { } budget && !token.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            if (!TryGetBounded(key, budget, true, out var previous)) throw;
            metrics.Record("degraded_read");
            logger.LogWarning(exception, "Returning a bounded degraded snapshot for {Dataset}/{Projection}.", dataset.Key, identity.Name);
            return Result<T>(previous!, identity, true, maximumAge);
        }
    }

    private bool TryGetBounded(ProjectionKey key, TimeSpan budget, bool allowKnownStale, out Entry? entry)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            if (_current.TryGetValue(key, out entry) && clock.GetElapsedTime(entry.Timestamp) <= budget
                && (allowKnownStale || _observed.TryGetValue(key.Dataset, out var known) && known.Version == entry.Version))
            {
                entry = entry with { Access = ++_access };
                _current[key] = entry;
                return true;
            }
            entry = null;
            return false;
        }
    }

    private Task<Entry> GetOrBuild(ProjectionKey key, Validation validation, Func<TContext, CancellationToken, Task<object>> load)
    {
        TaskCompletionSource<Entry> completion;
        lock (_gate)
        {
            ThrowIfStopped();
            if (_builds.TryGetValue((key, validation.Version), out var pending))
            {
                metrics.Record("joined_build");
                return pending;
            }
            if (_current.TryGetValue(key, out var cached) && cached.Version == validation.Version && IsObserved(validation))
                return Task.FromResult(cached);
            if (_builds.Count >= registration.MaximumPendingBuilds)
            {
                metrics.Record("capacity_rejected");
                throw new InvalidOperationException("Snapshot construction capacity is exhausted.");
            }
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _builds.Add((key, validation.Version), completion.Task);
        }
        _ = BuildAsync(key, validation, load, completion);
        return completion.Task;
    }

    private async Task BuildAsync(ProjectionKey key, Validation validation, Func<TContext, CancellationToken, Task<object>> load,
        TaskCompletionSource<Entry> completion)
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        deadline.CancelAfter(registration.BuildTimeout);
        var acquired = false;
        Entry? built = null;
        Exception? failure = null;
        try
        {
            await _parallelism.WaitAsync(deadline.Token);
            acquired = true;
            object value;
            await using (var db = await factory.CreateDbContextAsync(deadline.Token))
                value = await load(db, deadline.Token) ?? throw new InvalidOperationException("A snapshot loader returned null.");
            deadline.Token.ThrowIfCancellationRequested();
            // This fresh context also detects a loader that used a repeatable-read transaction internally.
            var after = await ValidateVersionAsync(key.Dataset, deadline.Token);
            if (after.Version != validation.Version) throw new SnapshotSourceChangedException();
            Entry entry;
            lock (_gate)
            {
                ThrowIfStopped();
                if (!IsObserved(after)) throw new SnapshotSourceChangedException();
                entry = new(value, after.Version, after.Timestamp, after.Utc, ++_access);
                _current[key] = entry;
                while (_current.Count > registration.MaximumCachedEntries)
                {
                    _current.Remove(_current.MinBy(x => x.Value.Access).Key);
                    metrics.Record("evicted");
                }
            }
            built = entry;
        }
        catch (OperationCanceledException exception) { failure = exception; }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Snapshot construction failed for {Dataset}/{Projection}.", key.Dataset, key.Identity.Name);
            failure = exception;
        }
        finally
        {
            if (acquired) _parallelism.Release();
            metrics.BuildCompleted(started);
            metrics.Record(failure is null ? "build_succeeded" : "build_failed");
            // Disposers must still see this build until all owned resources have been released.
            lock (_gate) _builds.Remove((key, validation.Version));
        }
        // Complete only after releasing owned resources so disposal cannot race a final semaphore release.
        if (failure is OperationCanceledException cancellation) completion.TrySetCanceled(cancellation.CancellationToken);
        else if (failure is not null)
        {
            completion.TrySetException(failure);
            _ = completion.Task.Exception;
        }
        else completion.TrySetResult(built!);
    }

    private async Task<Validation> ValidateVersionAsync(string dataset, CancellationToken token)
    {
        var ticket = Interlocked.Increment(ref _sequence);
        var timestamp = clock.GetTimestamp();
        var utc = clock.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(token);
        var started = Stopwatch.GetTimestamp();
        RepositorySnapshotVersion? row;
        try
        {
            row = await db.Set<RepositorySnapshotVersion>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.SourceId == registration.SourceId && x.DatasetKey == dataset, token);
        }
        catch { metrics.Record("validation_failed"); throw; }
        finally { metrics.ValidationCompleted(started); }
        if (row is null || row.Epoch == Guid.Empty || row.Revision < 0)
        {
            lock (_gate)
            {
                _observed.Remove(dataset);
                foreach (var key in _current.Keys.Where(key => key.Dataset == dataset).ToArray()) _current.Remove(key);
            }
            metrics.Record("authority_invalid");
            throw new SnapshotAuthorityException($"Snapshot authority '{registration.SourceId}/{dataset}' is missing or invalid. Run explicit deployment initialization.");
        }
        var version = new SnapshotVersion(row.SourceId, row.DatasetKey, row.Epoch, row.Revision);
        lock (_gate)
        {
            ThrowIfStopped();
            if (_observed.TryGetValue(dataset, out var previous))
            {
                if (previous.Version.Epoch == version.Epoch && previous.Version.Revision > version.Revision
                    || previous.Version.Epoch != version.Epoch && previous.Ticket > ticket)
                    throw new SnapshotSourceChangedException();
                _observed[dataset] = new(version, Math.Max(ticket, previous.Ticket));
            }
            else _observed.Add(dataset, new(version, ticket));
        }
        return new(version, timestamp, utc);
    }

    private bool IsObserved(Validation validation)
        => _observed.TryGetValue(validation.Version.DatasetKey, out var known) && known.Version == validation.Version;
    private SnapshotRead<T> Result<T>(Entry entry, SnapshotProjectionIdentity identity, bool degraded, TimeSpan? maximumAge) where T : class
    {
        var age = clock.GetElapsedTime(entry.Timestamp);
        // Validation itself can consume the caller's budget, including on a newly built result.
        if (maximumAge is { } budget && age > budget)
            throw new InvalidOperationException("Snapshot validation age exceeds the requested budget.");
        return new((T)entry.Value, entry.Version, identity, entry.ValidatedAtUtc, age, degraded);
    }
    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stopping.Token.ThrowIfCancellationRequested();
    }

    public RepositorySnapshotStatus GetStatus()
    {
        lock (_gate)
        {
            ThrowIfStopped();
            return new(registration.SourceId, registration.MaximumCachedEntries, _builds.Count, registration.MaximumPendingBuilds,
                _current.Select(pair => new RepositorySnapshotEntryStatus(pair.Value.Version, pair.Key.Identity,
                    pair.Value.ValidatedAtUtc, clock.GetElapsedTime(pair.Value.Timestamp))).ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _builds.Values.ToArray();
        }
        await _stopping.CancelAsync();
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Each build already records its outcome; shutdown drains owned work. */ }
        _stopping.Dispose();
        _parallelism.Dispose();
        lock (_gate) _current.Clear();
    }

    private sealed record ProjectionKey(string Dataset, SnapshotProjectionIdentity Identity);
    private sealed record Entry(object Value, SnapshotVersion Version, long Timestamp, DateTimeOffset ValidatedAtUtc, long Access);
    private sealed record Validation(SnapshotVersion Version, long Timestamp, DateTimeOffset Utc);
    private sealed record Observation(SnapshotVersion Version, long Ticket);
}
