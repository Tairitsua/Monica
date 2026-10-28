using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Monica.Locker.Leases.Metrics;
using Microsoft.Extensions.Logging;

namespace Monica.Locker.Leases;

/// <summary>Configures one process's reusable coordination leases.</summary>
public sealed record LeaseCoordinatorOptions
{
    /// <summary>Confirmed ownership duration, 30 seconds by default. Renewal runs every third of this duration.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Minimum interval between unsuccessful acquisitions, five seconds by default.</summary>
    public TimeSpan StandbyInterval { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Maximum wait for a store operation or the entire runtime drain, five seconds by default.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Rejects invalid intervals before starting coordination or background work.</summary>
    public void Validate()
    {
        if (Duration <= TimeSpan.Zero || Duration > TimeSpan.FromDays(1)
            || StandbyInterval <= TimeSpan.Zero || OperationTimeout <= TimeSpan.Zero
            || OperationTimeout >= Duration / 3)
            throw new ArgumentException("Lease duration must exceed three operation timeouts and be at most one day; all intervals must be positive.");
    }
}

/// <summary>Supplies an immutable acquisition and a cancellation signal that is never reset for a later term.</summary>
public sealed record LeaseContext(LeaseIdentity Identity, CancellationToken StoppingToken);

/// <summary>Caches role grants and supervises their renewal independently of protected work.</summary>
/// <remarks>
/// One owner keeps this instance for its worker lifetime. GetAsync admits work only under a confirmed term; pass the
/// term token to that work and validate its identity inside every protected database transaction. Cancellation alone
/// does not fence external effects. Stop and drain all caller-owned work before DisposeAsync releases grants.
/// Synchronous Dispose only stops supervision and leaves grants to expire; it never releases potentially active work.
/// </remarks>
public sealed class LeaseCoordinator : IDisposable, IAsyncDisposable
{
    private readonly IExclusiveLeaseStore _store;
    private readonly string _holder;
    private readonly TimeProvider _time;
    private readonly LeaseCoordinatorOptions _options;
    private readonly ILogger _logger;
    private readonly LeaseMetrics? _metrics;
    private readonly ConcurrentDictionary<LeaseResource, Entry> _entries = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _disposalGate = new();
    private Task? _disposal;
    private int _disposed;

    /// <summary>Creates an explicitly store-bound supervisor; it does not acquire roles until requested.</summary>
    /// <remarks>Pass the host-owned meter factory to enable shared lease metrics; null disables instrumentation.
    /// This coordinator neither owns nor disposes that factory. Metric tags never include resource or holder identities.</remarks>
    public LeaseCoordinator(IExclusiveLeaseStore store, string holderId, TimeProvider timeProvider,
        ILogger logger, LeaseCoordinatorOptions? options = null, IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (holderId.Length > 256) throw new ArgumentOutOfRangeException(nameof(holderId));
        _store = store;
        _holder = holderId;
        _time = timeProvider;
        _logger = logger;
        _options = options ?? new();
        _options.Validate();
        _metrics = LeaseMetrics.For(meterFactory);
    }

    /// <summary>Gets a usable cached term or attempts acquisition; returns null during contention/backoff.</summary>
    /// <remarks>Acquisition errors propagate. A stalled provider prevents further attempts for that resource until its call resolves.</remarks>
    public async Task<LeaseContext?> GetAsync(LeaseResource resource, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var entry = _entries.GetOrAdd(resource, static _ => new Entry());
        await entry.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (entry.Term is { } current && current.IsUsable) return current.Context;
            entry.Term?.Retire();
            if (entry.Renewal is { } previousRenewal)
                await previousRenewal.WaitAsync(_options.OperationTimeout, cancellationToken).ConfigureAwait(false);
            if (entry.Pending is { IsCompleted: false }
                || entry.Attempted && _time.GetElapsedTime(entry.LastAttempt) < _options.StandbyInterval) return null;
            entry.LastAttempt = _time.GetTimestamp();
            entry.Attempted = true;
            var started = _time.GetTimestamp();
            var wallStart = _time.GetUtcNow();
            var grant = await RequestAsync(entry, "acquire", token => _store.TryAcquireAsync(resource, _holder, _options.Duration, token),
                cancellationToken).ConfigureAwait(false);
            if (grant is null) return null;
            if (grant.Identity.Resource != resource || grant.Identity.HolderId != _holder)
                throw new InvalidOperationException("The lease provider returned a grant for a different resource or holder.");
            entry.Attempted = false;
            cancellationToken.ThrowIfCancellationRequested();
            _stopping.Token.ThrowIfCancellationRequested();
            var term = new Term(grant, started, wallStart, _time, _logger, _metrics);
            entry.Term = term;
            if (_stopping.IsCancellationRequested) term.Retire("stopped");
            if (!term.IsUsable) return null;
            entry.Renewal = Task.Run(() => RenewAsync(entry, term), CancellationToken.None);
            return term.Context;
        }
        finally { entry.Mutex.Release(); }
    }

    private async Task RenewAsync(Entry entry, Term term)
    {
        var retirementReason = "expired";
        try
        {
            while (term.IsUsable && !_stopping.IsCancellationRequested)
            {
                await Task.Delay(_options.Duration / 3, _time, term.Context.StoppingToken).ConfigureAwait(false);
                await entry.Mutex.WaitAsync(term.Context.StoppingToken).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(entry.Term, term) || !term.IsUsable) return;
                    var started = _time.GetTimestamp();
                    var wallStart = _time.GetUtcNow();
                    var renewed = await RequestAsync(entry, "renew",
                        token => _store.TryRenewAsync(term.Context.Identity, _options.Duration, token),
                        term.Context.StoppingToken).ConfigureAwait(false);
                    if (renewed is null || renewed.Identity != term.Context.Identity)
                    {
                        retirementReason = "renewal_rejected";
                        return;
                    }
                    if (!term.Confirm(renewed, started, wallStart)) return;
                }
                finally { entry.Mutex.Release(); }
            }
        }
        catch (OperationCanceledException) when (term.Context.StoppingToken.IsCancellationRequested || _stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            retirementReason = exception is TimeoutException ? "renewal_timeout" : "renewal_failed";
            _logger.LogWarning(exception, "Lease renewal failed for {Scope}/{Resource}; retiring its term.",
                term.Context.Identity.Resource.Scope, term.Context.Identity.Resource.Name);
        }
        finally { term.Retire(_stopping.IsCancellationRequested ? "stopped" : retirementReason); }
    }

    private async Task<LeaseGrant?> RequestAsync(Entry entry, string operationName, Func<CancellationToken, Task<LeaseGrant?>> operation,
        CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        var outcome = "failed";
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        // Isolate a provider's synchronous preamble as well as its asynchronous wait from the deadline supervisor.
        var pending = Task.Run(() => operation(request.Token), CancellationToken.None);
        entry.Pending = pending;
        _ = pending.ContinueWith(task =>
        {
            _ = task.Exception;
            request.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var grant = await pending.WaitAsync(_options.OperationTimeout, _time, cancellationToken).ConfigureAwait(false);
            outcome = grant is not null ? "success" : operationName == "acquire" ? "contended" : "lost";
            return grant;
        }
        catch (Exception exception)
        {
            outcome = LeaseMetrics.FailureResult(exception);
            // Do not wait for arbitrary cancellation callbacks. Retain Pending so unresolved I/O cannot accumulate.
            try { Observe(request.CancelAsync()); } catch (ObjectDisposedException) { }
            throw;
        }
        finally { _metrics?.RecordOperation(operationName, outcome, _time.GetElapsedTime(started)); }
    }

    /// <summary>Cancels local terms without releasing their database records.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Observe(_stopping.CancelAsync());
        foreach (var entry in _entries.Values) entry.Term?.Retire("stopped");
    }

    /// <summary>Stops supervision and conditionally releases grants after the caller has drained its protected work.</summary>
    /// <remarks>
    /// All resources share one real-time shutdown budget, independent of the workload clock. Each resource drains
    /// and releases independently; failures do not skip other resources. Uncertain grants are left to expire.
    /// Repeated or concurrent callers await the same cleanup task, including its failure.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_disposalGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var started = TimeProvider.System.GetTimestamp();
        Dispose();
        await Task.WhenAll(_entries.Values.Select(entry => DrainAndReleaseAsync(entry, started))).ConfigureAwait(false);
    }

    private async Task DrainAndReleaseAsync(Entry entry, long shutdownStarted)
    {
        if (!await entry.Mutex.WaitAsync(RemainingShutdownTime(shutdownStarted)).ConfigureAwait(false))
            throw new TimeoutException("Lease shutdown timed out waiting for an active request.");
        try
        {
            // No request can publish a later term after this point. Observe failed I/O before conditionally releasing
            // the known identity; a stalled operation leaves only this resource's grant to expire.
            await Task.WhenAll(entry.Renewal ?? Task.CompletedTask, entry.Pending ?? Task.CompletedTask)
                .ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .WaitAsync(RemainingShutdownTime(shutdownStarted)).ConfigureAwait(false);
            if (entry.Term is not { } term) return;
            await term.Cancellation.WaitAsync(RemainingShutdownTime(shutdownStarted)).ConfigureAwait(false);
            await ReleaseAsync(term.Context.Identity, RemainingShutdownTime(shutdownStarted)).ConfigureAwait(false);
        }
        finally { entry.Mutex.Release(); }
    }

    private TimeSpan RemainingShutdownTime(long started) =>
        TimeSpan.FromTicks(Math.Max(0, (_options.OperationTimeout - TimeProvider.System.GetElapsedTime(started)).Ticks));

    private async Task ReleaseAsync(LeaseIdentity identity, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Lease shutdown exhausted its release budget.");
        var started = _time.GetTimestamp();
        var outcome = "failed";
        using var deadline = new CancellationTokenSource(remaining);
        var releasing = Task.Run(() => _store.TryReleaseAsync(identity, deadline.Token), CancellationToken.None);
        Observe(releasing);
        try
        {
            outcome = await releasing.WaitAsync(remaining).ConfigureAwait(false) ? "success" : "lost";
        }
        catch (Exception exception)
        {
            outcome = exception is OperationCanceledException && deadline.IsCancellationRequested
                ? "timeout" : LeaseMetrics.FailureResult(exception);
            throw;
        }
        finally { _metrics?.RecordOperation("release", outcome, _time.GetElapsedTime(started)); }
    }

    private void Observe(Task task) => _ = task.ContinueWith(completed =>
        _logger.LogWarning(completed.Exception, "A lease background operation failed."), CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class Entry
    {
        internal SemaphoreSlim Mutex { get; } = new(1, 1);
        internal Term? Term;
        internal Task? Renewal;
        internal Task? Pending;
        internal long LastAttempt;
        internal bool Attempted;
    }

    private sealed class Term
    {
        private readonly object _gate = new();
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly LeaseMetrics? _metrics;
        private readonly CancellationTokenSource _lost = new();
        private readonly ITimer _deadline;
        private LeaseGrant _grant;
        private long _started;
        private DateTimeOffset _wallStart;
        private bool _retired;
        internal LeaseContext Context { get; }
        internal Task Cancellation { get; private set; } = Task.CompletedTask;

        internal Term(LeaseGrant grant, long started, DateTimeOffset wallStart, TimeProvider time, ILogger logger, LeaseMetrics? metrics)
        {
            _grant = grant;
            _started = started;
            _time = time;
            _wallStart = wallStart;
            _logger = logger;
            _metrics = metrics;
            Context = new LeaseContext(grant.Identity, _lost.Token);
            _deadline = time.CreateTimer(_ => Retire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Confirm(grant, started, wallStart);
        }

        internal bool IsUsable
        {
            get
            {
                lock (_gate)
                {
                    if (!_retired && Remaining <= TimeSpan.Zero) Retire();
                    return !_retired;
                }
            }
        }

        private TimeSpan Remaining
        {
            get
            {
                var elapsed = _time.GetElapsedTime(_started);
                var wallElapsed = _time.GetUtcNow() - _wallStart;
                if (wallElapsed > elapsed) elapsed = wallElapsed;
                return _grant.ValidFor - elapsed
                    - TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, _grant.ValidFor.Ticks / 10));
            }
        }

        internal bool Confirm(LeaseGrant grant, long started, DateTimeOffset wallStart)
        {
            lock (_gate)
            {
                if (_retired) return false;
                if (Remaining <= TimeSpan.Zero) { Retire(); return false; }
                _grant = grant;
                _started = started;
                _wallStart = wallStart;
                var remaining = Remaining;
                if (remaining <= TimeSpan.Zero) { Retire(); return false; }
                _deadline.Change(remaining, Timeout.InfiniteTimeSpan);
                return true;
            }
        }

        internal void Retire(string reason = "expired")
        {
            lock (_gate)
            {
                if (_retired) return;
                _retired = true;
                _deadline.Dispose();
                Cancellation = _lost.CancelAsync();
                _ = Cancellation.ContinueWith(task =>
                {
                    if (task.IsFaulted) _logger.LogWarning(task.Exception, "A lease-lost callback failed.");
                    _lost.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            _metrics?.RecordTermEnded(reason);
        }
    }
}
