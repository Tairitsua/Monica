using Monica.Core.Results;
using Monica.HealthCheck;
using Monica.HealthCheck.Models;

namespace Monica.HealthCheck.UI.State;

/// <summary>
/// Describes the availability of the first current-host health snapshot.
/// </summary>
public enum HealthCheckPageLoadState
{
    /// <summary>The first snapshot request has not completed.</summary>
    InitialLoading,

    /// <summary>A current-host snapshot is available.</summary>
    Ready,

    /// <summary>The first snapshot request failed.</summary>
    Failed
}

/// <summary>
/// Owns one Health Check page's snapshot, filters, detail selection, authorization rechecks, and refresh lifetime.
/// </summary>
public sealed class HealthCheckPageSession : IAsyncDisposable
{
    private static readonly TimeSpan DEFAULT_REFRESH_INTERVAL = TimeSpan.FromSeconds(10);

    private readonly Func<HealthCheckScope, CancellationToken, Task<Result<HealthCheckSnapshot>>> _getSnapshotAsync;
    private readonly Func<CancellationToken, Task<bool>> _authorize;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateGate = new();
    private volatile PageState _state = PageState.Initial;
    private PeriodicTimer? _refreshTimer;
    private Task? _refreshLoopTask;
    private Task<bool>? _refreshTask;
    private bool _disposed;

    internal HealthCheckPageSession(
        Func<HealthCheckScope, CancellationToken, Task<Result<HealthCheckSnapshot>>> getSnapshotAsync,
        Func<CancellationToken, Task<bool>> authorize,
        string hostName,
        TimeProvider? timeProvider = null,
        TimeSpan? refreshInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        _getSnapshotAsync = getSnapshotAsync ?? throw new ArgumentNullException(nameof(getSnapshotAsync));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        HostName = hostName;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _refreshInterval = refreshInterval ?? DEFAULT_REFRESH_INTERVAL;
        if (_refreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval), "The refresh interval must be positive.");
        }
    }

    /// <summary>
    /// Raised after an accepted page-state transition.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>Gets the current host label represented by every snapshot in this session.</summary>
    public string HostName { get; }

    /// <summary>Gets the latest sanitized health snapshot.</summary>
    public HealthCheckSnapshot? Snapshot => _state.Snapshot;

    /// <summary>Gets the state of the initial snapshot request.</summary>
    public HealthCheckPageLoadState LoadState => _state.LoadState;

    /// <summary>Gets the initial-load failure when no snapshot is available.</summary>
    public string? LoadError => _state.LoadError;

    /// <summary>Gets the most recent refresh failure while an older snapshot remains visible.</summary>
    public string? RefreshError => _state.RefreshError;

    /// <summary>Gets whether the current circuit failed its latest access evaluation.</summary>
    public bool IsAccessDenied => _state.IsAccessDenied;

    /// <summary>Gets whether a snapshot request currently owns the facade boundary.</summary>
    public bool IsRefreshing => _state.IsRefreshing;

    /// <summary>Gets the current name search.</summary>
    public string NameFilter => _state.NameFilter;

    /// <summary>Gets the optional health-state filter.</summary>
    public HealthCheckState? StatusFilter => _state.StatusFilter;

    /// <summary>Gets the optional registration-tag filter.</summary>
    public string? TagFilter => _state.TagFilter;

    /// <summary>Gets the entry selected for detail inspection.</summary>
    public HealthCheckEntrySnapshot? SelectedEntry => _state.SelectedEntry;

    /// <summary>Gets whether the entry detail drawer is open.</summary>
    public bool IsDetailsOpen => _state.IsDetailsOpen;

    /// <summary>Gets the distinct registration tags available in the latest snapshot.</summary>
    public IReadOnlyList<string> AvailableTags => Snapshot?.Entries
        .SelectMany(static entry => entry.Tags)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(static tag => tag, StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    /// <summary>Gets the entries after current name, state, and tag filters are applied.</summary>
    public IReadOnlyList<HealthCheckEntrySnapshot> FilteredEntries
    {
        get
        {
            var state = _state;
            IEnumerable<HealthCheckEntrySnapshot> entries = state.Snapshot?.Entries ?? [];
            if (!string.IsNullOrWhiteSpace(state.NameFilter))
            {
                entries = entries.Where(entry =>
                    entry.Name.Contains(state.NameFilter, StringComparison.OrdinalIgnoreCase) ||
                    (entry.Description?.Contains(state.NameFilter, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (state.StatusFilter is { } status)
            {
                entries = entries.Where(entry => entry.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(state.TagFilter))
            {
                entries = entries.Where(entry => entry.Tags.Contains(
                    state.TagFilter,
                    StringComparer.OrdinalIgnoreCase));
            }

            return entries
                .OrderByDescending(static entry => entry.Status)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Gets the aggregate state of registrations tagged for readiness.</summary>
    public HealthCheckState ReadinessStatus
    {
        get
        {
            var entries = Snapshot?.Entries
                .Where(static entry => entry.Tags.Contains(
                    HealthCheckTags.Ready,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray() ?? [];

            if (entries.Length == 0 || entries.Any(static entry => entry.Status == HealthCheckState.Unhealthy))
            {
                return HealthCheckState.Unhealthy;
            }

            return entries.Any(static entry => entry.Status == HealthCheckState.Degraded)
                ? HealthCheckState.Degraded
                : HealthCheckState.Healthy;
        }
    }

    /// <summary>Gets the number of registrations contributing to current readiness.</summary>
    public int ReadinessEntryCount => Snapshot?.Entries.Count(static entry => entry.Tags.Contains(
        HealthCheckTags.Ready,
        StringComparer.OrdinalIgnoreCase)) ?? 0;

    /// <summary>Loads the first snapshot and starts the owned ten-second refresh loop.</summary>
    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        await RefreshAsync();
        EnsureRefreshLoop();
    }

    /// <summary>
    /// Refreshes the current-host snapshot. Concurrent callers join the active request instead of starting another one.
    /// </summary>
    /// <returns><see langword="true"/> when a new snapshot replaced the previous one.</returns>
    public Task<bool> RefreshAsync()
    {
        lock (_stateGate)
        {
            ThrowIfDisposed();
            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            _refreshTask = ExecuteRefreshAsync(_lifetimeCancellation.Token);
            return _refreshTask;
        }
    }

    /// <summary>Updates the name search and notifies the owning page.</summary>
    public Task SetNameFilterAsync(string? value) => UpdatePresentationAsync(state => state with
    {
        NameFilter = value?.Trim() ?? string.Empty
    });

    /// <summary>Updates the optional state filter and notifies the owning page.</summary>
    public Task SetStatusFilterAsync(HealthCheckState? value) => UpdatePresentationAsync(state => state with
    {
        StatusFilter = value
    });

    /// <summary>Updates the optional tag filter and notifies the owning page.</summary>
    public Task SetTagFilterAsync(string? value) => UpdatePresentationAsync(state => state with
    {
        TagFilter = string.IsNullOrWhiteSpace(value) ? null : value
    });

    /// <summary>Opens the detail drawer for one health-check entry.</summary>
    public Task OpenDetailsAsync(HealthCheckEntrySnapshot entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return UpdatePresentationAsync(state => state with
        {
            SelectedEntry = entry,
            IsDetailsOpen = true
        });
    }

    /// <summary>Closes the health-check entry detail drawer.</summary>
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
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            refreshTimer = _refreshTimer;
            refreshLoopTask = _refreshLoopTask;
            refreshTask = _refreshTask;
            _refreshTimer = null;
            _refreshLoopTask = null;
        }

        await _lifetimeCancellation.CancelAsync();
        refreshTimer?.Dispose();

        if (refreshLoopTask is not null)
        {
            await refreshLoopTask;
        }

        if (refreshTask is not null)
        {
            await refreshTask;
        }

        Changed = null;
        _lifetimeCancellation.Dispose();
    }

    private async Task<bool> ExecuteRefreshAsync(CancellationToken cancellationToken)
    {
        BeginRefresh();
        await NotifyChangedAsync();

        try
        {
            if (!await _authorize(cancellationToken))
            {
                CompleteAccessDenied();
                await NotifyChangedAsync();
                return false;
            }

            var result = await _getSnapshotAsync(HealthCheckScope.All, cancellationToken);
            var succeeded = CompleteRefresh(result);
            await NotifyChangedAsync();
            return succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void BeginRefresh()
    {
        lock (_stateGate)
        {
            var hasSnapshot = _state.Snapshot is not null;
            _state = _state with
            {
                LoadState = hasSnapshot ? _state.LoadState : HealthCheckPageLoadState.InitialLoading,
                LoadError = hasSnapshot ? _state.LoadError : null,
                RefreshError = null,
                IsAccessDenied = false,
                IsRefreshing = hasSnapshot
            };
        }
    }

    private void CompleteAccessDenied()
    {
        lock (_stateGate)
        {
            _state = _state with
            {
                LoadState = _state.Snapshot is null ? HealthCheckPageLoadState.Failed : _state.LoadState,
                LoadError = null,
                RefreshError = null,
                IsAccessDenied = true,
                IsRefreshing = false
            };
        }
    }

    private bool CompleteRefresh(Result<HealthCheckSnapshot> result)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return false;
            }

            var error = result.WithoutData();
            var snapshot = result.Data;
            if (result.IsFailure() || snapshot is null)
            {
                var hasSnapshot = _state.Snapshot is not null;
                _state = _state with
                {
                    LoadState = hasSnapshot ? HealthCheckPageLoadState.Ready : HealthCheckPageLoadState.Failed,
                    LoadError = hasSnapshot ? null : error.Message ?? "The health check snapshot was unavailable.",
                    RefreshError = hasSnapshot ? error.Message ?? "The health check snapshot was unavailable." : null,
                    IsRefreshing = false
                };
                return false;
            }

            var selectedEntry = _state.SelectedEntry is null
                ? null
                : snapshot.Entries.FirstOrDefault(entry => string.Equals(
                    entry.Name,
                    _state.SelectedEntry.Name,
                    StringComparison.OrdinalIgnoreCase));
            _state = _state with
            {
                Snapshot = snapshot,
                LoadState = HealthCheckPageLoadState.Ready,
                LoadError = null,
                RefreshError = null,
                IsAccessDenied = false,
                IsRefreshing = false,
                SelectedEntry = selectedEntry,
                IsDetailsOpen = selectedEntry is not null && _state.IsDetailsOpen
            };
            return true;
        }
    }

    private void EnsureRefreshLoop()
    {
        lock (_stateGate)
        {
            if (_disposed || _refreshLoopTask is not null)
            {
                return;
            }

            _refreshTimer = new PeriodicTimer(_refreshInterval, _timeProvider);
            _refreshLoopTask = RunRefreshLoopAsync(_refreshTimer, _lifetimeCancellation.Token);
        }
    }

    private async Task RunRefreshLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync();
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

    private sealed record PageState(
        HealthCheckSnapshot? Snapshot,
        HealthCheckPageLoadState LoadState,
        string? LoadError,
        string? RefreshError,
        bool IsAccessDenied,
        bool IsRefreshing,
        string NameFilter,
        HealthCheckState? StatusFilter,
        string? TagFilter,
        HealthCheckEntrySnapshot? SelectedEntry,
        bool IsDetailsOpen)
    {
        public static PageState Initial { get; } = new(
            Snapshot: null,
            LoadState: HealthCheckPageLoadState.InitialLoading,
            LoadError: null,
            RefreshError: null,
            IsAccessDenied: false,
            IsRefreshing: false,
            NameFilter: string.Empty,
            StatusFilter: null,
            TagFilter: null,
            SelectedEntry: null,
            IsDetailsOpen: false);
    }
}
