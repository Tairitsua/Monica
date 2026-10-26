using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

/// <summary>
/// Defines the bounded ranges available from the scheduler statistics workspace.
/// </summary>
public enum SchedulerAnalyticsTimeRange
{
    /// <summary>
    /// Includes the current calendar day in the configured scheduler timezone.
    /// </summary>
    Today,

    /// <summary>
    /// Includes the rolling previous 24 hours.
    /// </summary>
    Last24Hours,

    /// <summary>
    /// Includes the rolling previous seven days.
    /// </summary>
    Last7Days,

    /// <summary>
    /// Includes the rolling previous 30 days.
    /// </summary>
    Last30Days
}

/// <summary>
/// Creates component-owned statistics sessions from circuit-scoped dependencies.
/// </summary>
internal sealed class SchedulerStatisticsPageStateFactory(
    JobSchedulerFacade facade,
    IJobSchedulerUiAccess access,
    TimeProvider timeProvider,
    SchedulerTimePresentation timePresentation)
{
    /// <summary>
    /// Creates a fresh cross-job analytics session.
    /// </summary>
    public SchedulerStatisticsPageState Create(string? ownerKey = null, string? jobKey = null,
        SchedulerAnalyticsTimeRange range = SchedulerAnalyticsTimeRange.Last24Hours) =>
        new(facade, access, timeProvider, timePresentation, ownerKey, jobKey, range);
}

/// <summary>
/// Owns authorization, range selection, bounded analytics loading, and async lifetime for the statistics route.
/// </summary>
/// <remarks>
/// Each read freezes its preset and UTC bounds before asynchronous authorization or serialization. Only the newest
/// request can replace accepted evidence. A failed refresh of the same preset retains its actual prior bounds and
/// observation, while a changed preset or lost access clears unrelated evidence. The component owns this session.
/// </remarks>
public sealed class SchedulerStatisticsPageState : IAsyncDisposable
{
    private readonly JobSchedulerFacade _facade;
    private readonly IJobSchedulerUiAccess _access;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulerTimePresentation _timePresentation;
    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _loadVersion;
    private int _accessVersion;
    private string? _ownerKey, _jobKey;

    internal SchedulerStatisticsPageState(
        JobSchedulerFacade facade,
        IJobSchedulerUiAccess access,
        TimeProvider timeProvider,
        SchedulerTimePresentation timePresentation, string? ownerKey = null, string? jobKey = null,
        SchedulerAnalyticsTimeRange range = SchedulerAnalyticsTimeRange.Last24Hours)
    {
        _facade = facade;
        _access = access;
        _timeProvider = timeProvider;
        _timePresentation = timePresentation;
        _ownerKey = ownerKey;
        _jobKey = jobKey;
        TimeRange = range;
    }

    /// <summary>
    /// Raised after an accepted state transition.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>
    /// Gets the newest requested preset, initially the rolling previous 24 hours.
    /// </summary>
    /// <remarks>Selection changes immediately; <see cref="LoadedTimeRange"/> identifies accepted evidence.</remarks>
    public SchedulerAnalyticsTimeRange TimeRange { get; private set; } = SchedulerAnalyticsTimeRange.Last24Hours;

    /// <summary>
    /// Gets the latest analytics snapshot.
    /// </summary>
    public JobExecutionAnalyticsSnapshot? Snapshot { get; private set; }

    /// <summary>Gets whether a successful snapshot, including an empty one, has been accepted.</summary>
    public bool HasLoadedSnapshot { get; private set; }

    /// <summary>Gets whether the newest read or authorization provider failed.</summary>
    /// <remarks>A failure remains explicit even when its result supplies no diagnostic message.</remarks>
    public bool IsQueryFailed { get; private set; }

    /// <summary>Gets the newest immutable query, with UTC bounds captured at admission.</summary>
    /// <remarks>The facade pins the configured scheduler scope; this query cannot select another scope.</remarks>
    public JobExecutionAnalyticsQuery? RequestedQuery { get; private set; }

    /// <summary>Gets the immutable query that produced the accepted snapshot.</summary>
    /// <remarks>A failed same-preset refresh does not relabel these bounds with its newer window.</remarks>
    public JobExecutionAnalyticsQuery? LoadedQuery { get; private set; }

    /// <summary>Gets the preset that produced accepted evidence, or null when evidence is unavailable.</summary>
    public SchedulerAnalyticsTimeRange? LoadedTimeRange { get; private set; }

    /// <summary>
    /// Gets whether authorization has completed.
    /// </summary>
    public bool AccessChecked { get; private set; }

    /// <summary>
    /// Gets whether the current circuit may inspect scheduler data.
    /// </summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>
    /// Gets whether analytics are currently loading.
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Gets the most recent failure, retaining prior evidence only for an authorized refresh of the same preset.
    /// </summary>
    /// <remarks><see cref="IsQueryFailed"/> remains true when a failed result has no diagnostic message.</remarks>
    public string? Error { get; private set; }

    /// <summary>
    /// Gets when the latest snapshot was accepted.
    /// </summary>
    public DateTimeOffset? ObservedAtUtc { get; private set; }

    /// <summary>Gets whether retirement has begun and new asynchronous admissions are canceled.</summary>
    public bool IsDisposed => _lifetime.IsRetired;

    /// <summary>
    /// Performs the first access-checked analytics load.
    /// </summary>
    /// <exception cref="OperationCanceledException">The session has retired or its read is canceled.</exception>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>
    /// Selects a preset immediately and reloads its frozen bounded query unless it is already requested.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The preset is not defined.</exception>
    /// <exception cref="OperationCanceledException">The session has retired or its read is canceled.</exception>
    public Task SetTimeRangeAsync(SchedulerAnalyticsTimeRange value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "Analytics time range is not supported.");
        return _lifetime.RunAsync(token => AdmitRefresh(value, token, skipRequestedPreset: true));
    }

    /// <summary>
    /// Reauthorizes and replaces the selected analytics snapshot.
    /// </summary>
    /// <exception cref="OperationCanceledException">The session has retired or its read is canceled.</exception>
    public Task RefreshAsync() => _lifetime.RunAsync(token => AdmitRefresh(TimeRange, token));

    /// <summary>Replaces the exact owner/job and preset intent, clearing evidence from the previous scope.</summary>
    /// <remarks>Owner and job identities remain opaque. Each replacement is captured before asynchronous authorization.</remarks>
    public Task SetQueryAsync(string? ownerKey, string? jobKey, SchedulerAnalyticsTimeRange range) =>
        _lifetime.RunAsync(token =>
        {
            if (!Enum.IsDefined(range)) throw new ArgumentOutOfRangeException(nameof(range));
            if (!_lifetime.TryApply(() => { _ownerKey = ownerKey; _jobKey = jobKey; ClearSnapshot(); })) throw new OperationCanceledException(token);
            return AdmitRefresh(range, token);
        });

    /// <summary>Checks current access before opening native inspection without loading analytics.</summary>
    /// <remarks>Denial or provider failure invalidates earlier authorized reads and clears inaccessible evidence.</remarks>
    /// <exception cref="OperationCanceledException">The session has retired or its check is canceled.</exception>
    public Task<bool> ReauthorizeAsync() => _lifetime.RunAsync(async token =>
    {
        var authorization = await EnsureAuthorizedAsync(token);
        await NotifyChangedAsync();
        return authorization.IsAuthorized;
    });

    private Task AdmitRefresh(SchedulerAnalyticsTimeRange range, CancellationToken token, bool skipRequestedPreset = false)
    {
        var version = 0;
        if (!_lifetime.TryApply(() =>
            {
                if (skipRequestedPreset && range == TimeRange) return;
                version = ++_loadVersion;
                TimeRange = range;
                RequestedQuery = null;
                if (LoadedTimeRange != range) ClearSnapshot();
                IsLoading = true;
                IsQueryFailed = false;
                Error = null;
            })) throw new OperationCanceledException(token);
        if (version == 0) return Task.CompletedTask;
        JobExecutionAnalyticsQuery query;
        try { query = CreateQuery(range, _timeProvider.GetUtcNow(), _timePresentation, _jobKey, _ownerKey); }
        catch (Exception exception)
        {
            if (!_lifetime.TryApply(() =>
                {
                    if (version != _loadVersion) return;
                    IsLoading = false;
                    IsQueryFailed = true;
                    Error = exception.Message;
                })) throw new OperationCanceledException(token);
            return NotifyChangedAsync();
        }
        if (!_lifetime.TryApply(() => { if (version == _loadVersion) RequestedQuery = query; }))
            throw new OperationCanceledException(token);
        return LoadAsync(range, query, version, token);
    }

    private async Task LoadAsync(
        SchedulerAnalyticsTimeRange range, JobExecutionAnalyticsQuery query, int version, CancellationToken token)
    {
        var entered = false;
        try
        {
            await NotifyChangedAsync();
            await _refreshGate.WaitAsync(token);
            entered = true;
            var authorization = await EnsureAuthorizedAsync(token);
            if (!authorization.IsAuthorized) return;

            var result = await _facade.GetExecutionAnalyticsAsync(query, token);
            token.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var snapshot))
            {
                FailQuery(version, authorization.Version, error.Message);
                return;
            }
            if (snapshot is null)
            {
                FailQuery(version, authorization.Version, null);
                return;
            }
            var observedAt = _timeProvider.GetUtcNow();
            if (!_lifetime.TryApply(() =>
                {
                    if (version != _loadVersion || !CanAcceptAccess(authorization.Version)) return;
                    Snapshot = snapshot;
                    LoadedQuery = query;
                    LoadedTimeRange = range;
                    HasLoadedSnapshot = true;
                    IsQueryFailed = false;
                    Error = null;
                    ObservedAtUtc = observedAt;
                })) throw new OperationCanceledException(token);
        }
        finally
        {
            if (entered) _refreshGate.Release();
            if (_lifetime.TryApply(() => { if (version == _loadVersion) IsLoading = false; }))
                await NotifyChangedAsync();
        }
    }

    private async Task<(bool IsAuthorized, int Version)> EnsureAuthorizedAsync(CancellationToken token)
    {
        var version = 0;
        if (!_lifetime.TryApply(() => version = _accessVersion)) throw new OperationCanceledException(token);
        try
        {
            var authorized = await _access.IsAuthorizedAsync(token);
            token.ThrowIfCancellationRequested();
            var accepted = false;
            if (!_lifetime.TryApply(() =>
                {
                    if (!authorized)
                    {
                        _accessVersion++;
                        IsAuthorized = false;
                        AccessChecked = true;
                        ClearSnapshot();
                        IsQueryFailed = false;
                        Error = null;
                        return;
                    }
                    if (version != _accessVersion) return;
                    IsAuthorized = true;
                    AccessChecked = true;
                    accepted = true;
                })) throw new OperationCanceledException(token);
            return (accepted, version);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() =>
                {
                    _accessVersion++;
                    IsAuthorized = false;
                    AccessChecked = true;
                    ClearSnapshot();
                    IsQueryFailed = true;
                    Error = exception.Message;
                })) throw new OperationCanceledException(token);
            return (false, version);
        }
    }

    private bool CanAcceptAccess(int version) => IsAuthorized && version == _accessVersion;

    private void FailQuery(int version, int accessVersion, string? error) => _lifetime.TryApply(() =>
    {
        if (version != _loadVersion || !CanAcceptAccess(accessVersion)) return;
        IsQueryFailed = true;
        Error = error;
    });

    private void ClearSnapshot()
    {
        Snapshot = null;
        HasLoadedSnapshot = false;
        LoadedQuery = null;
        LoadedTimeRange = null;
        ObservedAtUtc = null;
    }

    /// <summary>
    /// Creates the server-bounded analytics query for one UI preset.
    /// </summary>
    internal static JobExecutionAnalyticsQuery CreateQuery(
        SchedulerAnalyticsTimeRange range,
        DateTimeOffset now,
        SchedulerTimePresentation timePresentation,
        string? jobKey = null,
        string? ownerKey = null)
    {
        ArgumentNullException.ThrowIfNull(timePresentation);
        var end = now.ToUniversalTime();
        var (start, bucketSize) = range switch
        {
            SchedulerAnalyticsTimeRange.Today =>
                (timePresentation.GetStartOfSchedulerDayUtc(end), JobExecutionAnalyticsBucketSize.Hour),
            SchedulerAnalyticsTimeRange.Last24Hours =>
                (end.AddHours(-24), JobExecutionAnalyticsBucketSize.Hour),
            SchedulerAnalyticsTimeRange.Last7Days =>
                (end.AddDays(-7), JobExecutionAnalyticsBucketSize.Day),
            SchedulerAnalyticsTimeRange.Last30Days =>
                (end.AddDays(-30), JobExecutionAnalyticsBucketSize.Day),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Analytics time range is not supported.")
        };

        if (start >= end)
        {
            end = start.AddTicks(1);
        }

        return new JobExecutionAnalyticsQuery
        {
            StartTimeUtc = start,
            EndTimeUtc = end,
            BucketSize = bucketSize,
            OwnerKey = ownerKey,
            JobKey = jobKey,
            TopJobLimit = 10,
            SlowestExecutionLimit = 10
        };
    }

    /// <summary>Cancels and drains actual admitted work before releasing read serialization.</summary>
    /// <remarks>Repeated calls share the same completion, including unexpected failures.</remarks>
    /// <exception cref="AggregateException">Cancellation, admitted work, or cleanup failed unexpectedly.</exception>
    public ValueTask DisposeAsync()
    {
        Changed = null;
        return _lifetime.RetireAsync(() => _refreshGate.Dispose());
    }

    private async Task NotifyChangedAsync()
    {
        foreach (var handler in _lifetime.SnapshotHandlers(Changed)) await handler();
    }
}
