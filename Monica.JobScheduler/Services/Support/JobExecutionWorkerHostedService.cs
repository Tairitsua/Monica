using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;
using Monica.Modules;

namespace Monica.JobScheduler.Services.Support;

/// <summary>
/// Claims the selected logical owners' queued work and executes it under fenced leases with timeout, cooperative
/// cancellation, and retry handling. No broker delivery or process-local execution projection participates in
/// correctness.
/// </summary>
internal sealed class JobExecutionWorkerHostedService(
    IJobSchedulerStore store,
    IReadOnlyList<LocalJobDefinition> localDefinitions,
    JobOrchestrator orchestrator,
    JobSchedulerRuntimeState runtimeState,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    IOptions<ModuleJobSchedulerOption> schedulerOptions,
    IObservableInstanceRegistry observableRegistry,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<JobExecutionWorkerHostedService> logger)
    : MoBackgroundService(observableRegistry, hostedServiceOptions, serviceScopeFactory, logger)
{
    private readonly ConcurrentDictionary<string, Task> _executions = new(StringComparer.Ordinal);
    private readonly ModuleJobSchedulerOption _options = schedulerOptions.Value;
    private readonly IGrouping<string, LocalJobDefinition>[] _localOwners = localDefinitions
        .GroupBy(definition => definition.OwnerKey ?? schedulerOptions.Value.GetProjectName(), StringComparer.Ordinal)
        .OrderBy(static owner => owner.Key, StringComparer.Ordinal)
        .ToArray();
    private readonly CancellationTokenSource _workerStop = new();
    private int _nextClaimOwnerIndex;

    public override string? ServiceGroupId => nameof(ModuleJobScheduler);

    public override void Dispose()
    {
        _workerStop.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        using var linkedStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _workerStop.Token);
        var workerToken = linkedStop.Token;
        while (!workerToken.IsCancellationRequested)
        {
            try
            {
                ObserveCompletedExecutions();
                await ClaimAvailableWorkAsync(workerToken);
                runtimeState.SetInFlightExecutions(_executions.Count);
                if (runtimeState.SetWorker(
                        true,
                        $"Worker is active for {_localOwners.Length} logical owner(s) with {localDefinitions.Count} local job(s)."))
                {
                    RecordState(
                        $"Worker active with {_executions.Count} in-flight execution(s)",
                        HostedServiceState.Running);
                }
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _ = runtimeState.SetWorker(false, exception.Message);
                RecordState(
                    "Scheduler worker cycle failed",
                    HostedServiceState.Degraded,
                    exception,
                    LogLevel.Error);
            }

            await Task.Delay(_options.WorkerPollInterval, timeProvider, workerToken);
        }
    }

    protected override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        _workerStop.Cancel();
        _ = runtimeState.SetWorker(false, "Worker is stopping.");
        return Task.CompletedTask;
    }

    protected override async Task OnStoppedAsync(CancellationToken cancellationToken)
    {
        var tasks = _executions.Values.ToArray();
        if (tasks.Length != 0)
        {
            var shutdownDeadlineAlreadyReached = cancellationToken.IsCancellationRequested;
            if (shutdownDeadlineAlreadyReached)
            {
                RecordState(
                    "Worker shutdown reached the host deadline before execution cleanup completed",
                    HostedServiceState.Degraded,
                    logLevel: LogLevel.Warning);
            }

            try
            {
                // Keep the worker-owned grace period independent from the host token. A pre-cancelled host token
                // must not skip the bounded wait or the final task observation below.
                await Task.WhenAll(tasks).WaitAsync(_options.WorkerShutdownGracePeriod);
            }
            catch (TimeoutException)
            {
                RecordState(
                    $"{_executions.Count} execution(s) outlived the worker shutdown grace period",
                    HostedServiceState.Degraded,
                    logLevel: LogLevel.Warning);
                // Host shutdown is not a logical job cancellation. The linked execution token has already been
                // cancelled; a cooperative attempt releases its lease and requeues, while an uncooperative attempt
                // remains fenced until its lease expires and the scheduling plane recovers it.
            }
            catch (OperationCanceledException exception)
            {
                RecordState(
                    "One or more scheduler executions were cancelled during worker shutdown",
                    HostedServiceState.Degraded,
                    exception,
                    LogLevel.Warning);
            }
            catch (Exception exception)
            {
                RecordState(
                    "One or more scheduler executions faulted during worker shutdown",
                    HostedServiceState.Degraded,
                    exception,
                    LogLevel.Error);
            }
            finally
            {
                if (!shutdownDeadlineAlreadyReached && cancellationToken.IsCancellationRequested)
                {
                    RecordState(
                        "Worker shutdown reached the host deadline before execution cleanup completed",
                        HostedServiceState.Degraded,
                        logLevel: LogLevel.Warning);
                }

                ObserveCompletedExecutions();
            }
        }
    }

    internal async Task ClaimAvailableWorkAsync(CancellationToken workerToken)
    {
        var availableSlots = _options.MaxWorkerExecutionThreads - _executions.Count;
        if (availableSlots <= 0 || localDefinitions.Count == 0)
        {
            return;
        }

        var remainingBudget = Math.Min(availableSlots, _options.MaxClaimBatchSize);
        for (var visited = 0; visited < _localOwners.Length && remainingBudget > 0; visited++)
        {
            var owner = _localOwners[_nextClaimOwnerIndex];
            _nextClaimOwnerIndex = (_nextClaimOwnerIndex + 1) % _localOwners.Length;
            var leases = await store.ClaimAsync(new JobClaimRequest
            {
                SchedulerScopeKey = _options.SchedulerScopeKey,
                OwnerKey = owner.Key,
                WorkerInstanceId = _options.WorkerInstanceId,
                JobKeys = owner.Select(static definition => definition.Declaration.JobKey).ToArray(),
                LeaseDuration = _options.ExecutionLeaseDuration,
                MaxCount = remainingBudget
            }, workerToken);
            foreach (var lease in leases)
            {
                var task = ExecuteLeaseAsync(lease, workerToken);
                if (!_executions.TryAdd(lease.Execution.InstanceId, task))
                {
                    throw new InvalidOperationException(
                        $"Execution '{lease.Execution.InstanceId}' was claimed twice by one worker.");
                }

                remainingBudget--;
            }
        }
    }

    internal async Task ExecuteLeaseAsync(JobExecutionLease lease, CancellationToken workerToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(workerToken);
        var cancellationRequested = false;
        var timedOut = false;
        var executionTask = orchestrator.ExecuteAsync(lease, executionCancellation.Token);
        using var renewalStop = new CancellationTokenSource();
        var workerStopSignal = Task.Delay(Timeout.InfiniteTimeSpan, workerToken);
        var timeoutSignal = Task.Delay(
            lease.Execution.Template.MaxExecutionTimeout,
            timeProvider,
            renewalStop.Token);

        try
        {
            while (!executionTask.IsCompleted)
            {
                var renewalDelay = Task.Delay(
                    _options.ExecutionLeaseRenewInterval,
                    timeProvider,
                    renewalStop.Token);
                var completed = await Task.WhenAny(executionTask, renewalDelay, timeoutSignal, workerStopSignal);
                // A completion can race with a timeout, renewal, or shutdown signal. Prefer the finished job so a
                // successful attempt is not misclassified merely because another signal won Task.WhenAny's race.
                if (completed == executionTask || executionTask.IsCompleted)
                {
                    await renewalStop.CancelAsync();
                    try
                    {
                        await renewalDelay;
                    }
                    catch (OperationCanceledException) when (renewalStop.IsCancellationRequested)
                    {
                    }
                    break;
                }

                if (completed == workerStopSignal)
                {
                    if (!await CancelAndAwaitExecutionAsync(
                            executionCancellation,
                            executionTask,
                            lease.Execution.InstanceId,
                            "worker shutdown"))
                    {
                        return;
                    }

                    break;
                }

                if (completed == timeoutSignal)
                {
                    timedOut = true;
                    if (!await CancelAndAwaitExecutionAsync(
                            executionCancellation,
                            executionTask,
                            lease.Execution.InstanceId,
                            "the configured execution timeout"))
                    {
                        RequestWorkerRestart(
                            lease.Execution.InstanceId,
                            "Job code ignored cancellation after its execution timeout.");
                        return;
                    }

                    break;
                }

                JobLeaseRenewalResult renewal;
                try
                {
                    renewal = await store.RenewLeaseAsync(
                        lease.LeaseKey,
                        _options.ExecutionLeaseDuration,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    // A failed renewal call (serialization conflicts, a stalled store write) is not a lease loss:
                    // the lease usually still holds runway, so retry on the next cycle instead of cancelling
                    // cooperative job code. A genuinely lost lease surfaces as an explicit Lost result, and the
                    // scheduling plane recovers it if renewals never succeed again.
                    logger.LogWarning(
                        exception,
                        "Execution {InstanceId} lease renewal failed; retrying on the next worker cycle",
                        lease.Execution.InstanceId);
                    continue;
                }

                if (renewal.Status == JobLeaseRenewalStatus.Lost)
                {
                    if (!await CancelAndAwaitExecutionAsync(
                            executionCancellation,
                            executionTask,
                            lease.Execution.InstanceId,
                            "execution lease loss"))
                    {
                        RequestWorkerRestart(
                            lease.Execution.InstanceId,
                            "Job code ignored cancellation after its execution lease was lost.");
                    }

                    return;
                }

                if (renewal.Status == JobLeaseRenewalStatus.CancellationRequested)
                {
                    cancellationRequested = true;
                    if (!await CancelAndAwaitExecutionAsync(
                            executionCancellation,
                            executionTask,
                            lease.Execution.InstanceId,
                            "a durable cancellation request"))
                    {
                        RequestWorkerRestart(
                            lease.Execution.InstanceId,
                            "Job code ignored a durable cancellation request.");
                        return;
                    }

                    break;
                }
            }
        }
        catch
        {
            if (!await CancelAndAwaitExecutionAsync(
                    executionCancellation,
                    executionTask,
                    lease.Execution.InstanceId,
                    "a worker execution-loop failure"))
            {
                RequestWorkerRestart(
                    lease.Execution.InstanceId,
                    "Job code ignored cancellation after the worker execution loop failed.");
            }

            throw;
        }
        finally
        {
            await renewalStop.CancelAsync();
        }

        // The orchestrator normally converts user-code and pipeline exceptions into a failed result. Keep a second
        // boundary here because an exception can still escape before the orchestrator enters its guarded scope; a
        // faulted execution task must not strand the durable lease.
        var result = await ObserveExecutionTaskAsync(
            executionTask,
            lease.Execution.InstanceId,
            timedOut,
            cancellationRequested,
            workerToken);
        // Linked cancellation can finish a cooperative job before the shutdown signal wins Task.WhenAny. Requeue
        // only that cancellation-shaped completion; a successful task that finished before shutdown must be committed.
        var shutdownCancelledExecution = workerToken.IsCancellationRequested
                                         && !timedOut
                                         && !cancellationRequested
                                         && result.Outcome == JobAttemptOutcome.Cancelled;
        if (shutdownCancelledExecution)
        {
            var release = await store.ReleaseLeaseAsync(lease.LeaseKey, CancellationToken.None);
            WarnDiscardedOutcome(release.Status, lease.Execution.InstanceId, "shutdown release");
            return;
        }

        var outcome = timedOut
            ? JobAttemptOutcome.Failed
            : cancellationRequested
                ? JobAttemptOutcome.Cancelled
                : result.Outcome;
        var message = timedOut
            ? $"Execution timed out after {lease.Execution.Template.MaxExecutionTimeout}"
            : result.Message;
        var completion = await store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey,
            Outcome = outcome,
            Message = message,
            RetryDelay = _options.ExecutionRetryDelay
        }, CancellationToken.None);
        WarnDiscardedOutcome(completion.Status, lease.Execution.InstanceId, $"attempt outcome '{outcome}'");
    }

    private void WarnDiscardedOutcome(JobAttemptCompletionStatus status, string instanceId, string discarded)
    {
        // A lost completion means the scheduling plane already recovered the expired lease; this log is the only
        // local trace of an attempt outcome that will never reach the durable history.
        if (status != JobAttemptCompletionStatus.Lost)
        {
            return;
        }

        logger.LogWarning(
            "Execution {InstanceId} {Discarded} was discarded because its lease was already recovered by another host",
            instanceId,
            discarded);
    }

    private async Task<JobAttemptResult> ObserveExecutionTaskAsync(
        Task<JobAttemptResult> executionTask,
        string instanceId,
        bool timedOut,
        bool cancellationRequested,
        CancellationToken workerToken)
    {
        try
        {
            return await executionTask;
        }
        catch (OperationCanceledException exception) when (
            !timedOut
            && (cancellationRequested || workerToken.IsCancellationRequested))
        {
            return new JobAttemptResult(
                JobAttemptOutcome.Cancelled,
                exception.Message is { Length: > 0 } ? exception.Message : "Execution task was cancelled.");
        }
        catch (Exception exception)
        {
            RecordState(
                $"Worker execution '{instanceId}' pipeline faulted while reporting its outcome",
                HostedServiceState.Degraded,
                exception,
                LogLevel.Error);
            return new JobAttemptResult(JobAttemptOutcome.Failed, exception.ToString());
        }
    }

    private async Task<bool> CancelAndAwaitExecutionAsync(
        CancellationTokenSource executionCancellation,
        Task executionTask,
        string instanceId,
        string cancellationReason)
    {
        try
        {
            await executionCancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Execution {InstanceId} cancellation callbacks failed", instanceId);
        }

        var cancellationDeadline = Task.Delay(
            _options.ExecutionCancellationGracePeriod,
            timeProvider,
            CancellationToken.None);
        if (await Task.WhenAny(executionTask, cancellationDeadline) != executionTask)
        {
            logger.LogCritical(
                "Execution {InstanceId} did not exit within {GracePeriod} after {CancellationReason}",
                instanceId,
                _options.ExecutionCancellationGracePeriod,
                cancellationReason);
            return false;
        }

        try
        {
            await executionTask;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Execution {InstanceId} faulted while exiting after cancellation", instanceId);
        }

        return true;
    }

    private void RequestWorkerRestart(string instanceId, string reason)
    {
        var message = $"Worker restart requested because execution '{instanceId}' did not stop safely. {reason}";
        _ = runtimeState.SetWorker(false, message);
        RecordState(message, HostedServiceState.Faulted, logLevel: LogLevel.Critical);
        applicationLifetime.StopApplication();
    }

    private void ObserveCompletedExecutions()
    {
        foreach (var pair in _executions.Where(static pair => pair.Value.IsCompleted).ToArray())
        {
            if (_executions.TryRemove(pair.Key, out var task) && task.IsFaulted)
            {
                RecordState(
                    $"Worker execution '{pair.Key}' pipeline faulted",
                    HostedServiceState.Degraded,
                    task.Exception,
                    LogLevel.Error);
            }
        }
    }
}
