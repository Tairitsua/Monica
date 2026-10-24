using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Analytics;
using Microsoft.Extensions.Localization;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;

namespace Monica.JobScheduler.UI.UIJobScheduler.State;

/// <summary>
/// Carries one bounded execution stream together with its authoritative filtered total and recoverable load error.
/// </summary>
public sealed record JobExecutionActivitySlice
{
    /// <summary>
    /// Gets an empty successful stream.
    /// </summary>
    public static JobExecutionActivitySlice Empty { get; } = new();

    /// <summary>
    /// Gets the maximum number of newest executions requested and rendered for this stream.
    /// </summary>
    public int ItemLimit { get; init; } = 5;

    /// <summary>
    /// Gets the newest bounded executions rendered by the detail page.
    /// </summary>
    public IReadOnlyList<JobExecutionInstance> Items { get; init; } = [];

    /// <summary>
    /// Gets the authoritative number of executions matching the stream query.
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>
    /// Gets the latest recoverable query error while preserving previously accepted evidence.
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// Creates component-owned job-definition detail sessions from circuit-scoped dependencies.
/// </summary>
internal sealed class JobDefinitionDetailPageStateFactory(
    JobSchedulerFacade facade,
    IJobSchedulerUiAccess access,
    TimeProvider timeProvider,
    SchedulerTimePresentation timePresentation,
    IStringLocalizer<JobSchedulerResource> localizer)
{
    /// <summary>
    /// Creates a fresh detail state owned by one rendered route instance.
    /// </summary>
    public JobDefinitionDetailPageState Create(JobId jobId) =>
        new(facade, access, timeProvider, timePresentation, localizer, jobId);
}

/// <summary>
/// Owns one job-definition detail snapshot, authorization boundary, refresh serialization, and async lifetime.
/// </summary>
/// <remarks>
/// Authorization provider faults clear inaccessible evidence and become a local error. Mutations return a failed
/// result without calling the facade; authorization cancellation remains cancellation.
/// The rendered route owns this session. Retirement rejects new operations with canceled tasks and drains
/// admitted work before releasing its resources; repeated disposal shares that completion.
/// </remarks>
public sealed class JobDefinitionDetailPageState : IAsyncDisposable
{
    private readonly JobSchedulerFacade _facade;
    private readonly IJobSchedulerUiAccess _access;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulerTimePresentation _timePresentation;
    private readonly IStringLocalizer<JobSchedulerResource> _localizer;
    private readonly SchedulerOperationLifetime _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    internal JobDefinitionDetailPageState(
        JobSchedulerFacade facade,
        IJobSchedulerUiAccess access,
        TimeProvider timeProvider,
        SchedulerTimePresentation timePresentation,
        IStringLocalizer<JobSchedulerResource> localizer,
        JobId jobId)
    {
        jobId.Validate();
        _facade = facade;
        _access = access;
        _timeProvider = timeProvider;
        _timePresentation = timePresentation;
        _localizer = localizer;
        JobId = jobId;
    }

    /// <summary>
    /// Raised after an accepted state transition.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>
    /// Gets the exact definition identity represented by this state instance.
    /// </summary>
    public JobId JobId { get; }

    /// <summary>
    /// Gets the exact logical job key represented by this state instance.
    /// </summary>
    public string JobKey => JobId.JobKey;

    /// <summary>
    /// Gets the latest bounded operational projection.
    /// </summary>
    public JobOperationalSummary? Summary { get; private set; }

    /// <summary>
    /// Gets the latest 30-day bounded health projection for this job.
    /// </summary>
    public JobExecutionAnalyticsSnapshot? Analytics { get; private set; }

    /// <summary>
    /// Gets the newest bounded set of currently running executions for this logical job.
    /// </summary>
    public JobExecutionActivitySlice RunningActivity { get; private set; } = JobExecutionActivitySlice.Empty;

    /// <summary>
    /// Gets the newest bounded failed executions created within the health analytics window.
    /// </summary>
    public JobExecutionActivitySlice RecentFailureActivity { get; private set; } = JobExecutionActivitySlice.Empty;

    /// <summary>
    /// Gets whether the first access check completed.
    /// </summary>
    public bool AccessChecked { get; private set; }

    /// <summary>
    /// Gets whether the current circuit is authorized.
    /// </summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>
    /// Gets whether a refresh currently owns the facade boundary.
    /// </summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Gets whether an operator mutation currently owns the persistence boundary.
    /// </summary>
    public bool IsMutating { get; private set; }

    /// <summary>
    /// Gets the most recent load failure while preserving any prior snapshot.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    /// Gets the latest analytics-only load failure without hiding the operational dossier.
    /// </summary>
    public string? AnalyticsError { get; private set; }

    /// <summary>
    /// Gets whether the authorized lookup completed without finding a persisted definition.
    /// </summary>
    public bool IsNotFound { get; private set; }

    /// <summary>
    /// Gets when the current snapshot was accepted.
    /// </summary>
    public DateTimeOffset? ObservedAtUtc { get; private set; }

    /// <summary>
    /// Performs the first access-checked load.
    /// </summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>
    /// Re-evaluates authorization and replaces the operational snapshot when the read succeeds.
    /// </summary>
    /// <remarks>
    /// Every admitted refresh is drained on retirement. A retired owner rejects new calls with cancellation;
    /// load failures retain earlier accepted evidence and never adopt a result after retirement.
    /// </remarks>
    public Task RefreshAsync() => _lifetime.RunAsync(RefreshCoreAsync);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsLoading = true)) return;
            await NotifyChangedAsync();

            if (!(await EnsureAuthorizedAsync(cancellationToken)).IsAuthorized) return;

            var now = _timeProvider.GetUtcNow();
            var analyticsQuery = SchedulerStatisticsPageState.CreateQuery(
                SchedulerAnalyticsTimeRange.Last30Days,
                now,
                _timePresentation,
                JobKey,
                JobId.OwnerKey);
            var summaryTask = _facade.GetOperationalSummaryAsync(JobId, cancellationToken);
            var analyticsTask = _facade.GetExecutionAnalyticsAsync(analyticsQuery, cancellationToken);
            var runningTask = QueryExecutionsAsync(
                JobExecutionState.Running,
                JobExecutionSortField.StartedAtUtc,
                null,
                null,
                RunningActivity.ItemLimit,
                cancellationToken);
            var failuresTask = QueryExecutionsAsync(
                JobExecutionState.Failed,
                JobExecutionSortField.CompletedAtUtc,
                analyticsQuery.StartTimeUtc,
                analyticsQuery.EndTimeUtc.AddTicks(-1),
                RecentFailureActivity.ItemLimit,
                cancellationToken);
            await Task.WhenAll(summaryTask, analyticsTask, runningTask, failuresTask);
            cancellationToken.ThrowIfCancellationRequested();
            var summaryResult = await summaryTask;
            if (summaryResult.IsFailed(out var error, out var summary))
            {
                _lifetime.TryApply(() => Error = error.Message);
                return;
            }

            var analyticsResult = await analyticsTask;
            var runningResult = await runningTask;
            var failureResult = await failuresTask;
            _lifetime.TryApply(() =>
            {
                Summary = summary;
                IsNotFound = summary is null;
                Error = null;
                if (analyticsResult.IsFailed(out var analyticsError, out var analytics))
                    AnalyticsError = analyticsError.Message;
                else
                {
                    Analytics = analytics;
                    AnalyticsError = null;
                }
                RunningActivity = AcceptExecutionEvidence(runningResult, RunningActivity);
                RecentFailureActivity = AcceptExecutionEvidence(failureResult, RecentFailureActivity);
                ObservedAtUtc = now;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during route changes, page navigation, or host shutdown.
        }
        catch (Exception exception)
        {
            _lifetime.TryApply(() =>
            {
                if (!AccessChecked)
                {
                    ClearEvidence();
                    AccessChecked = true;
                    IsAuthorized = false;
                }
                Error = exception.Message;
            });
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsLoading = false)) await NotifyChangedAsync();
            }
            finally { _refreshGate.Release(); }
        }
    }

    /// <summary>
    /// Reauthorizes the circuit and replaces only the disabled policy override for the current persisted definition.
    /// </summary>
    /// <remarks>
    /// The clicked definition and policy revision are captured before asynchronous authorization.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The session has retired or the admitted operation is canceled.</exception>
    public Task<Res<JobPolicy>> SetDisabledAsync(bool disabled)
    {
        var summary = Summary;
        return _lifetime.RunAsync(token => SetDisabledCoreAsync(summary, disabled, token));
    }

    private async Task<Res<JobPolicy>> SetDisabledCoreAsync(
        JobOperationalSummary? summary, bool disabled, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsMutating = true))
                throw new OperationCanceledException(cancellationToken);
            await NotifyChangedAsync();

            var access = await EnsureAuthorizedAsync(cancellationToken);
            if (!access.IsAuthorized)
            {
                return access.Error ?? _localizer["Access:DeniedDescription"].Value;
            }

            if (summary is null)
            {
                return _localizer["JobDetail:NotFound", JobKey].Value;
            }

            if (disabled && JobSchedulerUiPresentation.IsManualOnlySuppressed(summary))
            {
                return _localizer["Catalog:Messages:PauseUnavailableManual"].Value;
            }

            var definition = summary.Definition;
            var result = await _facade.UpdatePolicyAsync(
                definition.OwnerKey,
                definition.Declaration.JobKey,
                new JobPolicyChange
                {
                    Overrides = definition.Policy.Overrides with { DisabledOverride = disabled },
                    ExpectedConcurrencyStamp = definition.Policy.ConcurrencyStamp
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsFailed(out _, out var policy))
            {
                var isRecurring = definition.Declaration.JobType == JobType.Recurring;
                var suspensionReasons = isRecurring
                    ? JobSchedulerUiPresentation.SetOperatorPolicySuspension(
                        summary.SuspensionReasons,
                        disabled)
                    : JobRecurringScheduleSuspensionReason.None;
                var observedAtUtc = _timeProvider.GetUtcNow();
                _lifetime.TryApply(() =>
                {
                    Summary = summary with
                    {
                        Definition = definition with { Policy = policy },
                        RecurringScheduleStatus = isRecurring
                            ? suspensionReasons != JobRecurringScheduleSuspensionReason.None
                                ? JobRecurringScheduleStatus.Suspended
                                : JobRecurringScheduleStatus.AwaitingSynchronization
                            : JobRecurringScheduleStatus.NotRecurring,
                        SuspensionReasons = suspensionReasons,
                        NextOccurrenceUtc = isRecurring ? null : summary.NextOccurrenceUtc
                    };
                    ObservedAtUtc = observedAtUtc;
                });
            }

            return result;
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsMutating = false)) await NotifyChangedAsync();
            }
            finally { _mutationGate.Release(); }
        }
    }

    /// <summary>
    /// Reauthorizes the circuit and admits an immediate occurrence for a present recurring definition without advancing its cursor.
    /// </summary>
    /// <exception cref="OperationCanceledException">The session has retired or the admitted operation is canceled.</exception>
    public Task<Res<JobExecutionInstance>> RunRecurringNowAsync()
    {
        var summary = Summary;
        return _lifetime.RunAsync(token => RunRecurringNowCoreAsync(summary, token));
    }

    private async Task<Res<JobExecutionInstance>> RunRecurringNowCoreAsync(
        JobOperationalSummary? summary, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifetime.TryApply(() => IsMutating = true))
                throw new OperationCanceledException(cancellationToken);
            await NotifyChangedAsync();

            var access = await EnsureAuthorizedAsync(cancellationToken);
            if (!access.IsAuthorized)
            {
                return access.Error ?? _localizer["Access:DeniedDescription"].Value;
            }

            if (summary is null)
            {
                return _localizer["JobDetail:NotFound", JobKey].Value;
            }

            var definition = summary.Definition;
            var result = await _facade.RunRecurringNowAsync(
                new JobRecurringRunNowRequest
                {
                    OwnerKey = definition.OwnerKey,
                    JobKey = definition.Declaration.JobKey
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsFailed(out _, out var execution))
            {
                var observedAtUtc = _timeProvider.GetUtcNow();
                _lifetime.TryApply(() =>
                {
                    Summary = summary with
                    {
                        LatestExecution = execution,
                        QueuedExecutionCount = summary.QueuedExecutionCount + 1
                    };
                    ObservedAtUtc = observedAtUtc;
                });
            }

            return result;
        }
        finally
        {
            try
            {
                if (_lifetime.TryApply(() => IsMutating = false)) await NotifyChangedAsync();
            }
            finally { _mutationGate.Release(); }
        }
    }

    /// <summary>
    /// Cancels and drains admitted work before releasing the session's serialization resources.
    /// </summary>
    /// <remarks>Repeated calls share the same completion, including any failure.</remarks>
    /// <exception cref="AggregateException">
    /// Cancellation callbacks, admitted operations, or resource cleanup failed unexpectedly.
    /// </exception>
    public ValueTask DisposeAsync()
    {
        Changed = null;
        return _lifetime.RetireAsync(() =>
        {
            _refreshGate.Dispose();
            _mutationGate.Dispose();
        });
    }

    internal bool IsDisposed => _lifetime.IsRetired;

    private async Task<(bool IsAuthorized, string? Error)> EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var authorized = await _access.IsAuthorizedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = _lifetime.TryApply(() =>
            {
                IsAuthorized = authorized;
                AccessChecked = true;
                if (!authorized) ClearEvidence();
            });
            return (accepted && authorized, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = string.IsNullOrWhiteSpace(exception.Message)
                ? _localizer["Access:DeniedDescription"].Value : exception.Message;
            _lifetime.TryApply(() =>
            {
                ClearEvidence();
                IsAuthorized = false;
                AccessChecked = true;
                Error = error;
            });
            return (false, error);
        }
    }

    private void ClearEvidence()
    {
        Summary = null;
        Analytics = null;
        RunningActivity = JobExecutionActivitySlice.Empty;
        RecentFailureActivity = JobExecutionActivitySlice.Empty;
        IsNotFound = false;
        Error = null;
        AnalyticsError = null;
        ObservedAtUtc = null;
    }

    private Task<Res<QueryResult<JobExecutionInstance>>> QueryExecutionsAsync(
        JobExecutionState state,
        JobExecutionSortField sortField,
        DateTimeOffset? createdAfterUtc,
        DateTimeOffset? createdBeforeUtc,
        int itemLimit,
        CancellationToken cancellationToken) =>
        _facade.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = string.Empty,
            OwnerKey = JobId.OwnerKey,
            JobKey = JobKey,
            States = [state],
            CreatedAfterUtc = createdAfterUtc,
            CreatedBeforeUtc = createdBeforeUtc,
            SortField = sortField,
            SortDescending = true,
            PageNumber = 1,
            PageSize = itemLimit
        }, cancellationToken);

    private static JobExecutionActivitySlice AcceptExecutionEvidence(
        Res<QueryResult<JobExecutionInstance>> result,
        JobExecutionActivitySlice current)
    {
        if (result.IsFailed(out var error, out var page))
        {
            return current with { Error = error.Message };
        }

        return new JobExecutionActivitySlice
        {
            Items = page.Items,
            TotalCount = page.TotalCount,
            ItemLimit = current.ItemLimit
        };
    }

    private async Task NotifyChangedAsync()
    {
        var handlers = _lifetime.SnapshotHandlers(Changed);
        foreach (var handler in handlers)
        {
            await handler();
        }
    }

}
