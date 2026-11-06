using Monica.Core.Results;
using Monica.UI.UISystemInfo.Models;

namespace Monica.UI.UISystemInfo.State;

internal sealed record SystemInfoFacadeCalls(
    Func<Result<SystemInfoSnapshot>> GetSnapshot,
    Func<Result> RequestSelfRestart);

/// <summary>
/// Describes the availability of the first immutable System Info snapshot.
/// </summary>
public enum SystemInfoPageLoadState
{
    /// <summary>The first host-local snapshot has not completed.</summary>
    InitialLoading,

    /// <summary>A snapshot is available for presentation.</summary>
    Ready,

    /// <summary>The first snapshot could not be obtained.</summary>
    Failed
}

/// <summary>
/// Owns the immutable snapshot and live presentation clock for one rendered System Info page.
/// </summary>
/// <remarks>
/// Snapshot capture is synchronous and host-local. Asynchrony is reserved for renderer notifications and the
/// page-owned one-second clock; the session never moves facade work to a background thread.
/// </remarks>
public sealed class SystemInfoPageSession : IAsyncDisposable
{
    private readonly SystemInfoFacadeCalls _calls;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _clockInterval;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateGate = new();
    private volatile PageState _state;
    private PeriodicTimer? _clockTimer;
    private Task? _clockTask;
    private int _requestInProgress;
    private int _disposed;

    internal SystemInfoPageSession(
        SystemInfoFacadeCalls calls,
        TimeProvider? timeProvider = null,
        TimeSpan? clockInterval = null)
    {
        _calls = calls;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clockInterval = clockInterval ?? TimeSpan.FromSeconds(1);
        if (_clockInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockInterval), "The clock interval must be positive.");
        }

        _state = PageState.Initial(_timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Raised after an accepted state transition. A failing renderer notification is detached so it cannot stop the
    /// page clock or prevent session disposal.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>Gets the latest immutable host snapshot.</summary>
    public SystemInfoSnapshot? Snapshot => _state.Snapshot;

    /// <summary>Gets the state of the initial snapshot request.</summary>
    public SystemInfoPageLoadState LoadState => _state.LoadState;

    /// <summary>Gets the initial-load failure when no snapshot is available.</summary>
    public string? LoadError => _state.LoadError;

    /// <summary>Gets the most recent refresh failure while the previous snapshot remains visible.</summary>
    public string? RefreshError => _state.RefreshError;

    /// <summary>Gets whether a refresh request currently owns the synchronous facade boundary.</summary>
    public bool IsRefreshing => _state.IsRefreshing;

    /// <summary>Gets whether the host accepted a self-restart request from this page.</summary>
    public bool RestartRequested => _state.RestartRequested;

    /// <summary>Gets the current page clock used by live presentation components.</summary>
    public DateTimeOffset NowUtc => _state.NowUtc;

    /// <summary>Loads the first snapshot and starts the page-owned clock after data becomes available.</summary>
    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        await RequestSnapshotAsync();
    }

    /// <summary>
    /// Refreshes the snapshot while preserving the currently rendered evidence.
    /// </summary>
    /// <returns><see langword="true"/> when a new snapshot replaced the previous one.</returns>
    public async Task<bool> RefreshAsync()
    {
        ThrowIfDisposed();
        return await RequestSnapshotAsync();
    }

    /// <summary>
    /// Requests the host-owned restart workflow and records acceptance for this page instance.
    /// </summary>
    /// <returns>The facade result describing whether the request was accepted.</returns>
    public Result RequestSelfRestart()
    {
        ThrowIfDisposed();
        var result = _calls.RequestSelfRestart();
        if (!result.IsFailure())
        {
            lock (_stateGate)
            {
                if (!IsDisposed)
                {
                    _state = _state with { RestartRequested = true };
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetimeCancellation.CancelAsync();
        PeriodicTimer? clockTimer;
        Task? clockTask;
        lock (_stateGate)
        {
            clockTimer = _clockTimer;
            clockTask = _clockTask;
            _clockTimer = null;
            _clockTask = null;
        }

        clockTimer?.Dispose();

        if (clockTask is not null)
        {
            await clockTask;
        }

        Changed = null;
        _lifetimeCancellation.Dispose();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private async Task<bool> RequestSnapshotAsync()
    {
        if (Interlocked.CompareExchange(ref _requestInProgress, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            BeginSnapshotRequest();
            await NotifyChangedAsync();
            if (IsDisposed)
            {
                return false;
            }

            var result = _calls.GetSnapshot();
            var succeeded = CompleteSnapshotRequest(result);
            if (Snapshot is not null)
            {
                EnsureClock();
            }

            await NotifyChangedAsync();
            return succeeded;
        }
        finally
        {
            Volatile.Write(ref _requestInProgress, 0);
        }
    }

    private void BeginSnapshotRequest()
    {
        lock (_stateGate)
        {
            var hasSnapshot = _state.Snapshot is not null;
            _state = _state with
            {
                LoadState = !hasSnapshot
                    ? SystemInfoPageLoadState.InitialLoading
                    : _state.LoadState,
                LoadError = hasSnapshot ? _state.LoadError : null,
                RefreshError = null,
                IsRefreshing = hasSnapshot
            };
        }
    }

    private bool CompleteSnapshotRequest(Result<SystemInfoSnapshot> result)
    {
        lock (_stateGate)
        {
            if (IsDisposed)
            {
                return false;
            }

            var error = result.WithoutData();
            var snapshot = result.Data;
            if (result.IsFailure())
            {
                var hasSnapshot = _state.Snapshot is not null;
                _state = _state with
                {
                    LoadState = hasSnapshot ? SystemInfoPageLoadState.Ready : SystemInfoPageLoadState.Failed,
                    LoadError = hasSnapshot ? null : error.Message,
                    RefreshError = hasSnapshot ? error.Message : null,
                    IsRefreshing = false
                };
                return false;
            }

            _state = _state with
            {
                Snapshot = snapshot,
                LoadState = SystemInfoPageLoadState.Ready,
                LoadError = null,
                RefreshError = null,
                IsRefreshing = false,
                NowUtc = _timeProvider.GetUtcNow()
            };
            return true;
        }
    }

    private void EnsureClock()
    {
        lock (_stateGate)
        {
            if (IsDisposed || _clockTask is not null)
            {
                return;
            }

            _clockTimer = new PeriodicTimer(_clockInterval, _timeProvider);
            _clockTask = RunClockAsync(_clockTimer, _lifetimeCancellation.Token);
        }
    }

    private async Task RunClockAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                lock (_stateGate)
                {
                    if (IsDisposed)
                    {
                        return;
                    }

                    _state = _state with { NowUtc = _timeProvider.GetUtcNow() };
                }

                await NotifyChangedAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the owning page is disposed.
        }
    }

    private async Task NotifyChangedAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        var subscribers = Changed?.GetInvocationList().Cast<Func<Task>>().ToArray() ?? [];
        foreach (var subscriber in subscribers)
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                await subscriber();
            }
            catch (Exception)
            {
                // Renderer and circuit lifetime failures must not terminate the session clock.
                Changed -= subscriber;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private sealed record PageState(
        SystemInfoPageLoadState LoadState,
        SystemInfoSnapshot? Snapshot,
        string? LoadError,
        string? RefreshError,
        bool IsRefreshing,
        bool RestartRequested,
        DateTimeOffset NowUtc)
    {
        internal static PageState Initial(DateTimeOffset nowUtc) => new(
            SystemInfoPageLoadState.InitialLoading,
            null,
            null,
            null,
            false,
            false,
            nowUtc);
    }
}
