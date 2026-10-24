using Monica.Core.Results;
using Monica.Framework.Seeder.Models;
using Monica.Framework.UI.UISeeder.Support;

namespace Monica.Framework.UI.UISeeder.State;

/// <summary>
/// Describes the availability of the first current-host Seeder diagnostics snapshot.
/// </summary>
public enum SeederPageLoadState
{
    /// <summary>The first snapshot request has not completed.</summary>
    InitialLoading,

    /// <summary>A current-host snapshot is available.</summary>
    Ready,

    /// <summary>The first snapshot request failed.</summary>
    Failed
}

/// <summary>
/// Owns one Seeder page's diagnostics, filters, detail selection, authorization rechecks, and polling lifetime.
/// </summary>
public sealed class SeederPageSession : IAsyncDisposable
{
    private static readonly TimeSpan DEFAULT_REFRESH_INTERVAL = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task<Res<SeederDiagnosticsSnapshot>>> _getSnapshotAsync;
    private readonly Func<string, CancellationToken, Task<Res<SeederStateSnapshot>>>? _runAsync;
    private readonly Func<CancellationToken, Task<bool>> _authorize;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateGate = new();
    private volatile PageState _state = PageState.Initial;
    private PeriodicTimer? _refreshTimer;
    private Task? _refreshLoopTask;
    private Task<bool>? _refreshTask;
    private Task<bool>? _executionTask;
    private bool _disposed;

    internal SeederPageSession(
        Func<CancellationToken, Task<Res<SeederDiagnosticsSnapshot>>> getSnapshotAsync,
        Func<CancellationToken, Task<bool>> authorize,
        string hostName,
        TimeProvider? timeProvider = null,
        TimeSpan? refreshInterval = null,
        Func<string, CancellationToken, Task<Res<SeederStateSnapshot>>>? runAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        _getSnapshotAsync = getSnapshotAsync ?? throw new ArgumentNullException(nameof(getSnapshotAsync));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _runAsync = runAsync;
        HostName = hostName;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? DEFAULT_REFRESH_INTERVAL;
        if (_refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval), "The refresh interval must be positive.");
        }
    }

    /// <summary>Raised after an accepted page-state transition.</summary>
    public event Func<Task>? Changed;

    /// <summary>Gets the current host label represented by every snapshot in this session.</summary>
    public string HostName { get; }

    /// <summary>Gets the latest sanitized current-host diagnostics snapshot.</summary>
    public SeederDiagnosticsSnapshot? Snapshot => _state.Snapshot;

    /// <summary>Gets the state of the initial snapshot request.</summary>
    public SeederPageLoadState LoadState => _state.LoadState;

    /// <summary>Gets the initial-load failure when no snapshot is available.</summary>
    public string? LoadError => _state.LoadError;

    /// <summary>Gets the most recent refresh failure while an older snapshot remains visible.</summary>
    public string? RefreshError => _state.RefreshError;

    /// <summary>Gets whether the current circuit failed its latest access evaluation.</summary>
    public bool IsAccessDenied => _state.IsAccessDenied;

    /// <summary>Gets whether a snapshot request currently owns the facade boundary.</summary>
    public bool IsRefreshing => _state.IsRefreshing;

    /// <summary>Gets whether this page owns an explicit execution request.</summary>
    public bool IsExecuting => _state.IsExecuting;

    /// <summary>Gets the last explicit execution admission or transport failure.</summary>
    public string? ExecutionError => _state.ExecutionError;

    /// <summary>Gets whether the selected local seeder can be submitted to the native executor.</summary>
    public bool CanRunSelected => _runAsync is not null && !_state.IsExecuting && !_state.IsAccessDenied &&
        SelectedSeeder is { Status: not SeederStatus.Disabled } &&
        Snapshot is { } snapshot && !SeederDisplay.IsActive(snapshot.Run.Status);

    /// <summary>Rechecks authorization and executes the selected seeder's dependency closure on this host.</summary>
    public Task<bool> RunSelectedAsync()
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            if (_executionTask is { IsCompleted: false } || !CanRunSelected) return Task.FromResult(false);
            var typeName = SelectedSeeder!.SeederTypeName;
            _executionTask = ExecuteSelectedAsync(typeName, _lifetimeCancellation.Token);
            return _executionTask;
        }
    }

    private async Task<bool> ExecuteSelectedAsync(string typeName, CancellationToken cancellationToken)
    {
        lock (_stateGate) _state = _state with { IsExecuting = true, ExecutionError = null };
        await NotifyChangedAsync();
        try
        {
            if (!await _authorize(cancellationToken))
            {
                TryCompleteAccessDenied();
                StopRefreshLoop();
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRefreshLoop();
            var result = await _runAsync!(typeName, cancellationToken);
            if (IsDisposed()) return false;
            if (result.IsFailed(out var error, out _))
            {
                lock (_stateGate) _state = _state with { ExecutionError = error.Message };
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            lock (_stateGate)
                if (!_disposed) _state = _state with { ExecutionError = exception.Message };
            return false;
        }
        finally
        {
            lock (_stateGate) _state = _state with { IsExecuting = false };
            if (!IsDisposed())
            {
                if (_refreshTask is { IsCompleted: false } pendingRefresh) await pendingRefresh;
                await RefreshAsync();
                await NotifyChangedAsync();
            }
        }
    }

    /// <summary>Gets the current name, dependency, or diagnostic search.</summary>
    public string SearchText => _state.SearchText;

    /// <summary>Gets the optional execution-status filter.</summary>
    public SeederStatus? StatusFilter => _state.StatusFilter;

    /// <summary>Gets the optional readiness-criticality filter.</summary>
    public SeederCriticality? CriticalityFilter => _state.CriticalityFilter;

    /// <summary>Gets the optional failure-behavior filter.</summary>
    public SeederFailureBehavior? FailureBehaviorFilter => _state.FailureBehaviorFilter;

    /// <summary>Gets the Seeder selected for detail inspection.</summary>
    public SeederExecutionSnapshot? SelectedSeeder
    {
        get
        {
            var state = _state;
            return state.SelectedSeederTypeName is null
                ? null
                : state.Snapshot?.Run.Seeders.FirstOrDefault(seeder => string.Equals(
                    seeder.SeederTypeName,
                    state.SelectedSeederTypeName,
                    StringComparison.Ordinal));
        }
    }

    /// <summary>Gets whether the Seeder detail drawer is open.</summary>
    public bool IsDetailsOpen => _state.IsDetailsOpen;

    /// <summary>Gets the seeders after current search and policy filters are applied.</summary>
    public IReadOnlyList<SeederExecutionSnapshot> FilteredSeeders
    {
        get
        {
            var state = _state;
            IEnumerable<SeederExecutionSnapshot> seeders = state.Snapshot?.Run.Seeders ?? [];
            var searchText = state.SearchText.Trim();
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                seeders = seeders.Where(seeder => MatchesSearch(seeder, searchText));
            }

            if (state.StatusFilter is { } status)
            {
                seeders = seeders.Where(seeder => seeder.Status == status);
            }

            if (state.CriticalityFilter is { } criticality)
            {
                seeders = seeders.Where(seeder => seeder.Criticality == criticality);
            }

            if (state.FailureBehaviorFilter is { } failureBehavior)
            {
                seeders = seeders.Where(seeder => seeder.FailureBehavior == failureBehavior);
            }

            return seeders
                .OrderBy(static seeder => StatusOrder(seeder.Status))
                .ThenBy(static seeder => seeder.SeederName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Gets terminal Seeder progress as a percentage in the inclusive 0-100 range.</summary>
    public double ProgressPercent
    {
        get
        {
            var run = Snapshot?.Run;
            if (run is null || run.TotalCount == 0)
            {
                return run?.IsCompleted == true ? 100 : 0;
            }

            return Math.Clamp(run.CompletedCount * 100d / run.TotalCount, 0, 100);
        }
    }

    /// <summary>Loads the first snapshot and starts polling only while the run is active.</summary>
    public Task InitializeAsync()
    {
        ThrowIfDisposed();
        return RefreshAsync();
    }

    /// <summary>
    /// Refreshes the current-host snapshot. Concurrent callers join the active request instead of starting another one.
    /// </summary>
    /// <returns><see langword="true"/> when a new snapshot replaced the previous one.</returns>
    public Task<bool> RefreshAsync()
    {
        return GetOrStartRefresh(throwIfDisposed: true)!;
    }

    private Task<bool>? GetOrStartRefresh(bool throwIfDisposed)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                if (throwIfDisposed)
                {
                    ThrowIfDisposed();
                }

                return null;
            }

            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            _refreshTask = ExecuteRefreshAsync(_lifetimeCancellation.Token);
            return _refreshTask;
        }
    }

    /// <summary>Updates the name, dependency, or diagnostic search and notifies the owning page.</summary>
    public Task SetSearchTextAsync(string? value) => UpdatePresentationAsync(state => state with
    {
        SearchText = value ?? string.Empty
    });

    /// <summary>Updates the optional execution-status filter.</summary>
    public Task SetStatusFilterAsync(SeederStatus? value) => UpdatePresentationAsync(state => state with
    {
        StatusFilter = value
    });

    /// <summary>Updates the optional readiness-criticality filter.</summary>
    public Task SetCriticalityFilterAsync(SeederCriticality? value) => UpdatePresentationAsync(state => state with
    {
        CriticalityFilter = value
    });

    /// <summary>Updates the optional failure-behavior filter.</summary>
    public Task SetFailureBehaviorFilterAsync(SeederFailureBehavior? value) => UpdatePresentationAsync(state => state with
    {
        FailureBehaviorFilter = value
    });

    /// <summary>Clears every presentation filter in one transition without changing the snapshot or selected details.</summary>
    public Task ClearFiltersAsync() => UpdatePresentationAsync(state => state with
    {
        SearchText = string.Empty,
        StatusFilter = null,
        CriticalityFilter = null,
        FailureBehaviorFilter = null
    });

    /// <summary>Opens the detail drawer for one Seeder.</summary>
    public Task OpenDetailsAsync(SeederExecutionSnapshot seeder)
    {
        ArgumentNullException.ThrowIfNull(seeder);
        return UpdatePresentationAsync(state => state with
        {
            SelectedSeederTypeName = seeder.SeederTypeName,
            IsDetailsOpen = true
        });
    }

    /// <summary>Closes the Seeder detail drawer.</summary>
    public Task CloseDetailsAsync() => UpdatePresentationAsync(state => state with
    {
        IsDetailsOpen = false
    });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        PeriodicTimer? refreshTimer;
        Task? refreshLoopTask;
        Task? refreshTask;
        Task? executionTask;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Changed = null;
            refreshTimer = _refreshTimer;
            refreshLoopTask = _refreshLoopTask;
            refreshTask = _refreshTask;
            executionTask = _executionTask;
            _refreshTimer = null;
            _refreshLoopTask = null;
        }

        try
        {
            await _lifetimeCancellation.CancelAsync();
        }
        finally
        {
            refreshTimer?.Dispose();
            try
            {
                if (refreshLoopTask is not null)
                {
                    await refreshLoopTask;
                }

                if (refreshTask is not null)
                {
                    await refreshTask;
                }
                if (executionTask is not null) await executionTask;
            }
            finally
            {
                _lifetimeCancellation.Dispose();
            }
        }
    }

    private async Task<bool> ExecuteRefreshAsync(CancellationToken cancellationToken)
    {
        BeginRefresh();
        await NotifyChangedAsync();

        try
        {
            var isAuthorized = await _authorize(cancellationToken);
            if (cancellationToken.IsCancellationRequested || IsDisposed())
            {
                return false;
            }

            if (!isAuthorized)
            {
                if (!TryCompleteAccessDenied())
                {
                    return false;
                }

                StopRefreshLoop();
                await NotifyChangedAsync();
                return false;
            }

            var result = await _getSnapshotAsync(cancellationToken);
            var succeeded = CompleteRefresh(result, out var runStatus);
            if (succeeded)
            {
                UpdateRefreshLoop(runStatus);
            }

            await NotifyChangedAsync();
            return succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            CompleteUnexpectedFailure(exception.Message);
            await NotifyChangedAsync();
            return false;
        }
    }

    private void BeginRefresh()
    {
        lock (_stateGate)
        {
            var hasSnapshot = _state.Snapshot is not null;
            // Keep the stale-data warning mounted while a refresh is in flight so repeated polling
            // failures do not remove and recreate the page's assertive alert.
            _state = _state with
            {
                LoadState = hasSnapshot ? _state.LoadState : SeederPageLoadState.InitialLoading,
                LoadError = hasSnapshot ? _state.LoadError : null,
                IsAccessDenied = false,
                IsRefreshing = hasSnapshot
            };
        }
    }

    private bool TryCompleteAccessDenied()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            _state = _state with
            {
                LoadState = _state.Snapshot is null ? SeederPageLoadState.Failed : _state.LoadState,
                LoadError = null,
                RefreshError = null,
                IsAccessDenied = true,
                IsRefreshing = false
            };
            return true;
        }
    }

    private bool CompleteRefresh(Res<SeederDiagnosticsSnapshot> result, out SeederRunStatus runStatus)
    {
        lock (_stateGate)
        {
            runStatus = _state.Snapshot?.Run.Status ?? SeederRunStatus.Waiting;
            if (_disposed)
            {
                return false;
            }

            if (result.IsFailed(out var error, out var snapshot))
            {
                CompleteFailureCore(error.Message ?? string.Empty);
                return false;
            }

            var selectionExists = _state.SelectedSeederTypeName is not null &&
                snapshot.Run.Seeders.Any(seeder => string.Equals(
                    seeder.SeederTypeName,
                    _state.SelectedSeederTypeName,
                    StringComparison.Ordinal));
            _state = _state with
            {
                Snapshot = snapshot,
                LoadState = SeederPageLoadState.Ready,
                LoadError = null,
                RefreshError = null,
                IsAccessDenied = false,
                IsRefreshing = false,
                SelectedSeederTypeName = selectionExists ? _state.SelectedSeederTypeName : null,
                IsDetailsOpen = selectionExists && _state.IsDetailsOpen
            };
            runStatus = snapshot.Run.Status;
            return true;
        }
    }

    private void CompleteUnexpectedFailure(string message)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            CompleteFailureCore(message);
        }
    }

    private void CompleteFailureCore(string message)
    {
        var hasSnapshot = _state.Snapshot is not null;
        _state = _state with
        {
            LoadState = hasSnapshot ? SeederPageLoadState.Ready : SeederPageLoadState.Failed,
            LoadError = hasSnapshot ? null : message,
            RefreshError = hasSnapshot ? message : null,
            IsRefreshing = false
        };
    }

    private void UpdateRefreshLoop(SeederRunStatus status)
    {
        if (IsExecuting || SeederDisplay.IsActive(status))
        {
            EnsureRefreshLoop();
            return;
        }

        StopRefreshLoop();
    }

    private void EnsureRefreshLoop()
    {
        lock (_stateGate)
        {
            if (_disposed || _refreshLoopTask is { IsCompleted: false })
            {
                return;
            }

            _refreshTimer = new PeriodicTimer(_refreshInterval, _timeProvider);
            _refreshLoopTask = RunRefreshLoopAsync(_refreshTimer, _lifetimeCancellation.Token);
        }
    }

    private void StopRefreshLoop()
    {
        PeriodicTimer? timer;
        lock (_stateGate)
        {
            timer = _refreshTimer;
            _refreshTimer = null;
        }

        timer?.Dispose();
    }

    private async Task RunRefreshLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var refreshTask = GetOrStartRefresh(throwIfDisposed: false);
                if (refreshTask is null)
                {
                    return;
                }

                await refreshTask;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the owning page is disposed.
        }
    }

    private async Task UpdatePresentationAsync(Func<PageState, PageState> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_stateGate)
        {
            ThrowIfDisposed();
            _state = update(_state);
        }

        await NotifyChangedAsync();
    }

    private async Task NotifyChangedAsync()
    {
        var handlers = Changed?.GetInvocationList().Cast<Func<Task>>().ToArray() ?? [];
        foreach (var handler in handlers)
        {
            if (IsDisposed())
            {
                return;
            }

            try
            {
                await handler();
            }
            catch
            {
                Changed -= handler;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool IsDisposed()
    {
        lock (_stateGate)
        {
            return _disposed;
        }
    }

    private static bool MatchesSearch(SeederExecutionSnapshot seeder, string searchText)
    {
        return seeder.SeederName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               seeder.SeederTypeName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
               seeder.Dependencies.Any(dependency => dependency.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
               (seeder.ErrorType?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (seeder.ErrorMessage?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static int StatusOrder(SeederStatus status) => status switch
    {
        SeederStatus.Failed => 0,
        SeederStatus.Blocked => 1,
        SeederStatus.Cancelled => 2,
        SeederStatus.Running => 3,
        SeederStatus.Pending => 4,
        SeederStatus.Held => 5,
        SeederStatus.Succeeded => 6,
        _ => 7
    };

    private sealed record PageState(
        SeederDiagnosticsSnapshot? Snapshot,
        SeederPageLoadState LoadState,
        string? LoadError,
        string? RefreshError,
        bool IsAccessDenied,
        bool IsRefreshing,
        string SearchText,
        SeederStatus? StatusFilter,
        SeederCriticality? CriticalityFilter,
        SeederFailureBehavior? FailureBehaviorFilter,
        string? SelectedSeederTypeName,
        bool IsDetailsOpen,
        bool IsExecuting = false,
        string? ExecutionError = null)
    {
        public static PageState Initial { get; } = new(
            Snapshot: null,
            LoadState: SeederPageLoadState.InitialLoading,
            LoadError: null,
            RefreshError: null,
            IsAccessDenied: false,
            IsRefreshing: false,
            SearchText: string.Empty,
            StatusFilter: null,
            CriticalityFilter: null,
            FailureBehaviorFilter: null,
            SelectedSeederTypeName: null,
            IsDetailsOpen: false);
    }
}
