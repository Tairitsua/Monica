using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;

namespace Monica.JobScheduler.Facades;

/// <summary>
/// Exposes the durable scheduler operational surface to host APIs and the JobScheduler UI.
/// </summary>
/// <remarks>
/// The facade never reconstructs scheduler state from events or caches. Every operation delegates to the unified
/// scheduler store, which owns admission, definition, policy, execution, lease, and cancellation atomicity.
/// </remarks>
public sealed class JobSchedulerFacade(
    IJobSchedulerStore store,
    IOptions<ModuleJobSchedulerOption> options,
    TimeProvider timeProvider,
    JobSchedulerRuntimeState runtimeState,
    ILogger<JobSchedulerFacade> logger,
    IReadOnlyList<LocalJobDefinition>? localDefinitions = null,
    ITypeActivationPolicy? activation = null)
{
    private const int MAX_PAGE_SIZE = 200;
    private const int RECENT_EXECUTION_COUNT = 12;
    private const int OVERVIEW_DEFINITION_LIMIT = 500;
    private const int THROUGHPUT_WINDOW_HOURS = 24;

    /// <summary>
    /// Multiplier applied to <see cref="ModuleJobSchedulerOption.SnapshotSyncInterval"/> to derive the owner
    /// definition-observation freshness threshold. Stale publication evidence does not prove process failure.
    /// </summary>
    private const int OWNER_ONLINE_THRESHOLD_SNAPSHOT_MULTIPLIER = 3;

    private readonly string _schedulerScopeKey = options.Value.SchedulerScopeKey;

    /// <summary>
    /// Captures the serving host's effective configuration, storage identity, local scheduling-plane readiness,
    /// and every owner's durable footprint in the configured scope.
    /// </summary>
    /// <param name="cancellationToken">Cancels snapshot loading.</param>
    /// <returns>A result containing the host-scoped runtime snapshot.</returns>
    public Task<Res<JobSchedulerRuntimeOverview>> GetRuntimeOverviewAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var option = options.Value;
            var owners = await store.QueryOwnerSummariesAsync(_schedulerScopeKey, cancellationToken);
            return new JobSchedulerRuntimeOverview
            {
                Configuration = new JobSchedulerRuntimeConfiguration
                {
                    SchedulerScopeKey = option.SchedulerScopeKey,
                    OwnerKey = option.GetProjectName(),
                    OwnerKeys = GetLocalOwnerKeys(),
                    WorkerInstanceId = option.WorkerInstanceId,
                    RecurringJobDebugMode = option.RecurringJobDebugMode,
                    CronTimeZoneId = option.CronTimeZone.Id,
                    SchedulingPollInterval = option.SchedulingPollInterval,
                    WorkerPollInterval = option.WorkerPollInterval,
                    SnapshotSyncInterval = option.SnapshotSyncInterval,
                    ExecutionLeaseDuration = option.ExecutionLeaseDuration,
                    ExecutionLeaseRenewInterval = option.ExecutionLeaseRenewInterval,
                    ExecutionRetryDelay = option.ExecutionRetryDelay,
                    ExecutionCancellationGracePeriod = option.ExecutionCancellationGracePeriod,
                    WorkerShutdownGracePeriod = option.WorkerShutdownGracePeriod,
                    MaxWorkerExecutionThreads = option.MaxWorkerExecutionThreads,
                    MaxClaimBatchSize = option.MaxClaimBatchSize,
                    MaxRecurringMaterializationsPerCycle = option.MaxRecurringMaterializationsPerCycle,
                    MaxExpiredLeaseRecoveriesPerCycle = option.MaxExpiredLeaseRecoveriesPerCycle,
                    EnableHistoryCleanup = option.EnableHistoryCleanup,
                    HistoryCleanupInterval = option.HistoryCleanupInterval,
                    MaxHistoryDeletionsPerCycle = option.MaxHistoryDeletionsPerCycle,
                    MaxExecutionHistoryEntriesPerExecution = option.MaxExecutionHistoryEntriesPerExecution,
                    MaxExecutionHistoryMessageLength = option.MaxExecutionHistoryMessageLength,
                    MaxRetainedOrphanedExecutions = option.MaxRetainedOrphanedExecutions
                },
                Store = DescribeStore(),
                LocalPlane = new JobSchedulerLocalPlaneSnapshot
                {
                    SchedulingReady = runtimeState.SchedulingReady,
                    SchedulingMessage = runtimeState.SchedulingMessage,
                    WorkerReady = runtimeState.WorkerReady,
                    WorkerMessage = runtimeState.WorkerMessage,
                    InFlightExecutions = runtimeState.InFlightExecutions
                },
                Owners = owners,
                OwnerOnlineThreshold = option.SnapshotSyncInterval * OWNER_ONLINE_THRESHOLD_SNAPSHOT_MULTIPLIER,
                CapturedAtUtc = timeProvider.GetUtcNow()
            };
        }, "load the scheduler runtime overview", cancellationToken);

    private IReadOnlyList<string> GetLocalOwnerKeys()
    {
        var owners = (localDefinitions ?? [])
            .Select(definition => definition.OwnerKey ?? options.Value.GetProjectName())
            .Concat(activation?.ActiveWorkloadOwners ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (activation is null && owners.Count == 0)
            owners.Add(options.Value.GetProjectName());
        return owners.Order(StringComparer.Ordinal).ToArray();
    }

    private JobSchedulerStoreInfo DescribeStore() =>
        store is IJobSchedulerStoreDescriptor descriptor
            ? new JobSchedulerStoreInfo { Kind = descriptor.StoreKind, Provider = descriptor.Provider }
            : new JobSchedulerStoreInfo { Kind = store.GetType().Name };

    /// <summary>
    /// Captures persisted definitions, queue state, and recent execution activity for the configured scope.
    /// </summary>
    /// <param name="cancellationToken">Cancels snapshot loading.</param>
    /// <returns>A result containing one bounded operational snapshot.</returns>
    public Task<Res<JobSchedulerOverview>> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async () =>
        {
            var definitionsTask = store.QueryDefinitionsAsync(
                _schedulerScopeKey,
                new JobDefinitionQuery
                {
                    PageNumber = 1,
                    PageSize = OVERVIEW_DEFINITION_LIMIT,
                    SortField = JobDefinitionSortField.OwnerKey
                },
                cancellationToken);
            var statisticsTask = store.GetExecutionStateStatisticsAsync(
                _schedulerScopeKey,
                cancellationToken: cancellationToken);
            var recentTask = store.QueryExecutionsAsync(
                new JobExecutionQuery
                {
                    SchedulerScopeKey = _schedulerScopeKey,
                    PageNumber = 1,
                    PageSize = RECENT_EXECUTION_COUNT,
                    SortField = JobExecutionSortField.CreatedAtUtc,
                    SortDescending = true
                },
                cancellationToken);
            // Page size one is sufficient: only TotalCount feeds the per-hour rate.
            var throughputTask = store.QueryExecutionsAsync(
                new JobExecutionQuery
                {
                    SchedulerScopeKey = _schedulerScopeKey,
                    CreatedAfterUtc = timeProvider.GetUtcNow().AddHours(-THROUGHPUT_WINDOW_HOURS),
                    PageNumber = 1,
                    PageSize = 1,
                    SortField = JobExecutionSortField.CreatedAtUtc,
                    SortDescending = true
                },
                cancellationToken);

            await Task.WhenAll(definitionsTask, statisticsTask, recentTask, throughputTask);
            return new JobSchedulerOverview
            {
                Definitions = (await definitionsTask).Items,
                ExecutionStateCounts = await statisticsTask,
                RecentExecutions = (await recentTask).Items,
                ExecutionsPerHourLast24h = (await throughputTask).TotalCount / (double)THROUGHPUT_WINDOW_HOURS,
                CapturedAtUtc = timeProvider.GetUtcNow()
            };
        }, "load the scheduler overview", cancellationToken);

    /// <summary>
    /// Queries persisted definitions together with their independent operator policies.
    /// </summary>
    public Task<Res<QueryResult<JobDefinition>>> QueryDefinitionsAsync(
        JobDefinitionQuery query,
        CancellationToken cancellationToken = default)
    {
        var bounded = BoundDefinitionQuery(query);
        return ExecuteAsync(
            () => store.QueryDefinitionsAsync(_schedulerScopeKey, bounded, cancellationToken),
            "query job definitions",
            cancellationToken);
    }

    /// <summary>
    /// Queries definitions together with recurring, latest-execution, and active-queue operational signals.
    /// </summary>
    /// <param name="query">Definition filters and page bounds.</param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    /// <returns>A result containing the bounded operational page.</returns>
    public Task<Res<QueryResult<JobOperationalSummary>>> QueryOperationalSummariesAsync(
        JobDefinitionQuery query,
        CancellationToken cancellationToken = default)
    {
        var bounded = BoundDefinitionQuery(query);
        return ExecuteAsync(
            () => store.QueryOperationalSummariesAsync(_schedulerScopeKey, bounded, cancellationToken),
            "query job operational summaries",
            cancellationToken);
    }

    /// <summary>
    /// Gets the operational projection for one exact job definition.
    /// </summary>
    /// <param name="jobId">The definition identity.</param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    /// <returns>A result containing the projection, or <see langword="null"/> when the identity is unknown.</returns>
    public Task<Res<JobOperationalSummary?>> GetOperationalSummaryAsync(
        JobId jobId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => store.GetOperationalSummaryAsync(_schedulerScopeKey, jobId, cancellationToken),
            "load a job operational summary",
            cancellationToken);

    /// <summary>
    /// Gets one definition by its exact identity.
    /// </summary>
    public Task<Res<JobDefinition?>> GetDefinitionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => store.GetDefinitionAsync(_schedulerScopeKey, jobId.OwnerKey, jobId.JobKey, cancellationToken),
            "load a job definition",
            cancellationToken);

    /// <summary>
    /// Replaces only operator-owned policy fields using optimistic concurrency.
    /// </summary>
    public Task<Res<JobPolicy>> UpdatePolicyAsync(
        string ownerKey,
        string jobKey,
        JobPolicyChange change,
        CancellationToken cancellationToken = default) =>
        ExecutePolicyUpdateAsync(
            () => store.UpdatePolicyAsync(_schedulerScopeKey, ownerKey, jobKey, change, cancellationToken),
            jobKey,
            cancellationToken);

    /// <summary>
    /// Replaces policies for a bounded set of jobs and returns an ordered result for every item.
    /// </summary>
    /// <remarks>
    /// Each item retains the store's optimistic concurrency boundary. A validation or concurrency failure does not
    /// roll back successful items, enabling operators to refresh and retry only stale selections.
    /// </remarks>
    /// <param name="request">The bounded ordered set of complete policy replacements.</param>
    /// <param name="cancellationToken">Cancels the batch between items.</param>
    /// <returns>A result containing one ordered success or failure outcome per item.</returns>
    public Task<Res<JobPolicyBatchUpdateResult>> UpdatePoliciesAsync(
        JobPolicyBatchUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => UpdatePoliciesCoreAsync(request, cancellationToken),
            "update job policies",
            cancellationToken);

    /// <summary>
    /// Admits one execution for a present definition through the durable queue.
    /// </summary>
    public Task<Res<JobExecutionInstance>> TriggerAsync(
        JobTriggerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var enqueueRequest = new JobEnqueueRequest
        {
            SchedulerScopeKey = _schedulerScopeKey,
            OwnerKey = request.OwnerKey,
            JobKey = request.JobKey,
            InstanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? Guid.NewGuid().ToString("N")
                : request.InstanceId,
            JobArgs = request.JobArgs,
            AvailableAtUtc = request.AvailableAtUtc,
            EnqueueReason = "Operator-triggered execution admitted"
        };
        return ExecuteAsync(
            () => store.EnqueueAsync(enqueueRequest, cancellationToken),
            "trigger a job execution",
            cancellationToken);
    }

    /// <summary>
    /// Queues one immediate execution of a recurring job without changing its recurring schedule cursor.
    /// A paused schedule remains eligible for this explicit operator action.
    /// </summary>
    public Task<Res<JobExecutionInstance>> RunRecurringNowAsync(
        JobRecurringRunNowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = new JobRecurringRunNowCommand
        {
            SchedulerScopeKey = _schedulerScopeKey,
            OwnerKey = request.OwnerKey,
            JobKey = request.JobKey,
            InstanceId = string.IsNullOrWhiteSpace(request.InstanceId)
                ? Guid.NewGuid().ToString("N")
                : request.InstanceId
        };
        return ExecuteAsync(
            () => store.RunRecurringNowAsync(command, cancellationToken),
            "run a recurring job immediately",
            cancellationToken);
    }

    /// <summary>
    /// Queries durable executions through a bounded page. The configured scheduler scope always overrides caller data.
    /// </summary>
    public Task<Res<QueryResult<JobExecutionInstance>>> QueryExecutionsAsync(
        JobExecutionQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var bounded = query with
        {
            SchedulerScopeKey = _schedulerScopeKey,
            PageNumber = Math.Max(1, query.PageNumber),
            PageSize = Math.Clamp(query.PageSize, 1, MAX_PAGE_SIZE)
        };
        return ExecuteAsync(
            () => store.QueryExecutionsAsync(bounded, cancellationToken),
            "query job executions",
            cancellationToken);
    }

    /// <summary>
    /// Gets bounded execution analytics for the configured scheduler scope.
    /// </summary>
    /// <param name="query">The required time range, interval, optional owner and job filters, and ranking bounds.</param>
    /// <param name="cancellationToken">Cancels analytics loading.</param>
    /// <returns>A result containing cohort, outcome, duration, trend, ranking, and slow-attempt analytics.</returns>
    public Task<Res<JobExecutionAnalyticsSnapshot>> GetExecutionAnalyticsAsync(
        JobExecutionAnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ExecuteAsync(
            () => store.GetExecutionAnalyticsAsync(_schedulerScopeKey, query, cancellationToken),
            "load job execution analytics",
            cancellationToken);
    }

    private static JobDefinitionQuery BoundDefinitionQuery(JobDefinitionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query with
        {
            PageNumber = Math.Max(1, query.PageNumber),
            PageSize = Math.Clamp(query.PageSize, 1, MAX_PAGE_SIZE)
        };
    }

    private async Task<JobPolicyBatchUpdateResult> UpdatePoliciesCoreAsync(
        JobPolicyBatchUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Items);
        if (request.Items.Count is < 1 or > JobPolicyBatchUpdateRequest.MAX_ITEM_COUNT)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Items.Count,
                $"A policy batch must contain between 1 and {JobPolicyBatchUpdateRequest.MAX_ITEM_COUNT} items.");
        }

        var results = new List<JobPolicyBatchUpdateItemResult>(request.Items.Count);
        foreach (var item in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            try
            {
                var policy = await store.UpdatePolicyAsync(
                    _schedulerScopeKey,
                    item.OwnerKey,
                    item.JobKey,
                    item.ToChange(),
                    cancellationToken);
                results.Add(new JobPolicyBatchUpdateItemResult
                {
                    OwnerKey = item.OwnerKey,
                    JobKey = item.JobKey,
                    Policy = policy
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Failed to update policy for job {JobKey} owned by {OwnerKey} in a batch.",
                    item.JobKey,
                    item.OwnerKey);
                results.Add(new JobPolicyBatchUpdateItemResult
                {
                    OwnerKey = item.OwnerKey,
                    JobKey = item.JobKey,
                    Error = exception.GetMessageRecursively(),
                    FailureStatus = ResolvePolicyFailureStatus(exception)
                });
            }
        }

        return new JobPolicyBatchUpdateResult { Items = results };
    }

    /// <summary>
    /// Gets one durable execution including its append-only history.
    /// </summary>
    public Task<Res<JobExecutionInstance?>> GetExecutionAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => store.GetExecutionAsync(_schedulerScopeKey, instanceId, cancellationToken),
            "load a job execution",
            cancellationToken);

    /// <summary>
    /// Cancels queued work immediately or records a cooperative request for the current fenced worker.
    /// </summary>
    public Task<Res<JobCancellationResult>> CancelExecutionAsync(
        string instanceId,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => store.RequestCancellationAsync(
                _schedulerScopeKey,
                instanceId,
                reason ?? "Cancellation requested by an operator",
                cancellationToken),
            "cancel a job execution",
            cancellationToken);

    private async Task<Res<T>> ExecuteAsync<T>(
        Func<Task<T>> action,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return Res.Ok(await action());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to {SchedulerOperation}.", operation);
            return Res.Fail($"Failed to {operation}: {exception.GetMessageRecursively()}");
        }
    }

    private async Task<Res<JobPolicy>> ExecutePolicyUpdateAsync(
        Func<Task<JobPolicy>> action,
        string jobKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return Res.Ok(await action());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JobPolicyConcurrencyException exception)
        {
            logger.LogWarning(exception, "Policy update for job {JobKey} lost optimistic concurrency.", jobKey);
            return new Res<JobPolicy>(exception.GetMessageRecursively(), ResStatus.Conflict);
        }
        catch (JobDefinitionNotFoundException exception)
        {
            logger.LogWarning(exception, "Policy update target {JobKey} was not found.", jobKey);
            return new Res<JobPolicy>(exception.GetMessageRecursively(), ResStatus.NotFound);
        }
        catch (ArgumentException exception)
        {
            logger.LogWarning(exception, "Policy update for job {JobKey} failed validation.", jobKey);
            return new Res<JobPolicy>(exception.GetMessageRecursively(), ResStatus.BadRequest);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update policy for job {JobKey}.", jobKey);
            return new Res<JobPolicy>(
                $"Failed to update job policy: {exception.GetMessageRecursively()}",
                ResStatus.InternalError);
        }
    }

    private static ResStatus ResolvePolicyFailureStatus(Exception exception) => exception switch
    {
        JobPolicyConcurrencyException => ResStatus.Conflict,
        JobDefinitionNotFoundException => ResStatus.NotFound,
        ArgumentException => ResStatus.BadRequest,
        _ => ResStatus.InternalError
    };
}
