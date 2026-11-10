using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Metrics;
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
    ILogger<JobExecutionWorkerHostedService> logger,
    IMeterFactory? meterFactory = null)
    : MoBackgroundService(observableRegistry, hostedServiceOptions, serviceScopeFactory, logger)
{
    private readonly ConcurrentDictionary<string, Task> _executions = new(StringComparer.Ordinal);
    private readonly Channel<bool> _completionWake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly ModuleJobSchedulerOption _options = schedulerOptions.Value;
    private readonly JobExecutionWorkerMetrics? _metrics = meterFactory is null ? null : new(meterFactory);
    private readonly LocalOwner[] _localOwners = localDefinitions
        .GroupBy(definition => definition.OwnerKey ?? schedulerOptions.Value.GetProjectName(), StringComparer.Ordinal)
        .OrderBy(static owner => owner.Key, StringComparer.Ordinal)
        .Select(owner => new LocalOwner(
            owner.Key,
            owner.Select(static definition => definition.Declaration.JobKey).ToArray(),
            new JobOwnerSnapshot(
                schedulerOptions.Value.SchedulerScopeKey,
                owner.Key,
                owner.Select(static definition => definition.Declaration).ToArray()).ComputeManifestHash()))
        .ToArray();
    private readonly CancellationTokenSource _workerStop = new();
    private readonly object _workerStateGate = new();
    private int _admissionsStopped;
    private int _pendingStoreDrains;
    private long? _storeRecoveryStarted;
    private bool _storeRecoveryRequiresBackoff;
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
        var idleRounds = 0;
        while (!workerToken.IsCancellationRequested && Volatile.Read(ref _admissionsStopped) == 0)
        {
            var claimedWork = false;
            try
            {
                ObserveCompletedExecutions();
                var beforeClaim = _executions.Count;
                await ClaimAvailableWorkAsync(workerToken);
                claimedWork = _executions.Count > beforeClaim;
                runtimeState.SetInFlightExecutions(_executions.Count);
                lock (_workerStateGate)
                {
                    if (Volatile.Read(ref _admissionsStopped) != 0 || workerToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (CanAdmitWork() && _storeRecoveryStarted is null && runtimeState.SetWorker(
                            true,
                            $"Worker is active for {_localOwners.Length} logical owner(s) with {localDefinitions.Count} local job(s)."))
                    {
                        RecordState(
                            $"Worker active with {_executions.Count} in-flight execution(s)",
                            HostedServiceState.Running);
                    }
                }
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                lock (_workerStateGate)
                {
                    if (Volatile.Read(ref _admissionsStopped) == 0)
                    {
                        _ = runtimeState.SetWorker(false, exception.Message);
                        RecordState(
                            "Scheduler worker cycle failed",
                            HostedServiceState.Degraded,
                            exception,
                            LogLevel.Error);
                    }
                }
            }

            idleRounds = claimedWork ? 0 : Math.Min(idleRounds + 1, 30);
            await WaitForWorkerWakeAsync(GetPollDelay(idleRounds), workerToken);
        }
    }

    private TimeSpan GetPollDelay(int idleRounds)
    {
        var rampTicks = TimeSpan.FromMilliseconds(250).Ticks * Math.Pow(2, Math.Max(0, idleRounds - 1));
        var maximumTicks = (long)Math.Min(_options.WorkerPollInterval.Ticks, rampTicks);
        var jitteredTicks = (long)(maximumTicks * (0.8 + Random.Shared.NextDouble() * 0.2));
        return TimeSpan.FromTicks(Math.Max(1, Math.Min(maximumTicks, jitteredTicks)));
    }

    private async Task WaitForWorkerWakeAsync(TimeSpan delay, CancellationToken workerToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(workerToken);
        var poll = Task.Delay(delay, timeProvider, waitCancellation.Token);
        var completion = _completionWake.Reader.ReadAsync(waitCancellation.Token).AsTask();
        try
        {
            await Task.WhenAny(poll, completion);
        }
        finally
        {
            await waitCancellation.CancelAsync();
            try
            {
                await Task.WhenAll(poll, completion);
            }
            catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
            {
            }
        }

        workerToken.ThrowIfCancellationRequested();
    }

    protected override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        _workerStop.Cancel();
        lock (_workerStateGate)
        {
            runtimeState.SetWorkerAdmissionPaused(false);
            _ = runtimeState.SetWorker(false, "Worker is stopping.");
        }
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
                await Task.WhenAll(tasks).WaitAsync(_options.WorkerShutdownGracePeriod, timeProvider);
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
        if (availableSlots <= 0 || localDefinitions.Count == 0
            || !CanAdmitWork() || workerToken.IsCancellationRequested)
        {
            return;
        }

        var remainingBudget = Math.Min(availableSlots, _options.MaxClaimBatchSize);
        for (var visited = 0; visited < _localOwners.Length && remainingBudget > 0; visited++)
        {
            if (!CanAdmitWork() || workerToken.IsCancellationRequested)
            {
                return;
            }

            var owner = _localOwners[_nextClaimOwnerIndex];
            _nextClaimOwnerIndex = (_nextClaimOwnerIndex + 1) % _localOwners.Length;
            var requestStarted = timeProvider.GetTimestamp();
            long? recoveryStarted;
            lock (_workerStateGate)
            {
                recoveryStarted = _storeRecoveryStarted;
            }
            var leases = await store.ClaimAsync(new JobClaimRequest
            {
                SchedulerScopeKey = _options.SchedulerScopeKey,
                OwnerKey = owner.Key,
                OwnerManifestHash = owner.ManifestHash,
                WorkerInstanceId = _options.WorkerInstanceId,
                JobKeys = owner.JobKeys,
                LeaseDuration = _options.ExecutionLeaseDuration,
                MaxCount = recoveryStarted.HasValue ? 1 : remainingBudget
            }, workerToken);
            lock (_workerStateGate)
            {
                if (recoveryStarted.HasValue && _storeRecoveryStarted == recoveryStarted && _pendingStoreDrains == 0
                    && Volatile.Read(ref _admissionsStopped) == 0)
                {
                    _storeRecoveryStarted = null;
                    _storeRecoveryRequiresBackoff = false;
                    _metrics?.Record("StoreAdmissionResumed");
                    logger.LogInformation("Scheduler worker admission resumed after store recovery. ReasonCode={ReasonCode}",
                        "StoreAdmissionResumed");
                }
            }
            foreach (var lease in leases)
            {
                var task = !CanAdmitWork() || workerToken.IsCancellationRequested
                    ? FinalizeAsync(lease, null)
                    : ExecuteLeaseAsync(lease, workerToken, requestStarted);
                if (!_executions.TryAdd(lease.Execution.InstanceId, NotifyExecutionCompletedAsync(task)))
                {
                    throw new InvalidOperationException(
                        $"Execution '{lease.Execution.InstanceId}' was claimed twice by one worker.");
                }

                remainingBudget--;
            }

            if (recoveryStarted.HasValue)
            {
                return;
            }
        }
    }

    internal async Task ExecuteLeaseAsync(
        JobExecutionLease lease,
        CancellationToken workerToken,
        long? claimRequestStartedTimestamp = null)
    {
        using var executionCancellation = new CancellationTokenSource();
        using var timers = new CancellationTokenSource();
        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(workerToken, _workerStop.Token);
        var grantStarted = claimRequestStartedTimestamp ?? timeProvider.GetTimestamp();
        var stopAfter = GetStopAfter(lease.GrantedAtUtc, lease.Execution.LeaseExpiresAtUtc);
        var remaining = stopAfter - timeProvider.GetElapsedTime(grantStarted);
        if (remaining <= TimeSpan.Zero || workerStop.IsCancellationRequested
                                      || !CanAdmitWork())
        {
            await FinalizeAsync(lease, null);
            return;
        }

        // Timers precede dispatch, including a synchronous user-code preamble or queued thread-pool dispatch.
        var watchdogCancellation = new CancellationTokenSource();
        var watchdog = Task.Delay(remaining, timeProvider, watchdogCancellation.Token);
        var timeoutSignal = Task.Delay(lease.Execution.Template.MaxExecutionTimeout, timeProvider, timers.Token);
        var workerStopSignal = Task.Delay(Timeout.InfiniteTimeSpan, workerStop.Token);
        var never = Task.Delay(Timeout.InfiniteTimeSpan, timers.Token);
        var renewalDelay = Task.Delay(_options.ExecutionLeaseRenewInterval, timeProvider, timers.Token);
        var executionTask = Task.Run(
            () => orchestrator.ExecuteAsync(lease, executionCancellation.Token),
            executionCancellation.Token);
        StoreOperation<JobLeaseRenewalResult>? renewal = null;
        Task? renewalDrain = null;
        var renewalDeadline = never;
        var renewalStarted = 0L;
        var cancellationRequested = false;
        var timedOut = false;
        var leaseRetired = false;

        async Task CancelExecutionAsync(string reason)
        {
            // Store callbacks and their timeout stay independent from the job's cancellation callbacks.
            if (renewal is not null)
            {
                renewalDrain ??= DrainStoreOperationAsync(renewal, lease, "renewal", cancel: true);
            }
            await CancelAndAwaitExecutionAsync(
                executionCancellation,
                executionTask,
                lease.Execution.InstanceId,
                reason,
                GetCancellationGrace(grantStarted, stopAfter));
        }

        try
        {
            while (!executionTask.IsCompleted)
            {
                var completed = await Task.WhenAny(
                    executionTask, watchdog, timeoutSignal, workerStopSignal,
                    renewalDrain ?? renewal?.Completion ?? renewalDelay, renewalDeadline);
                if (executionTask.IsCompleted)
                {
                    break;
                }

                // Retirement is irreversible, including a renewal arriving in the same event-loop turn.
                if (watchdog.IsCompleted || completed == workerStopSignal || completed == timeoutSignal)
                {
                    leaseRetired = watchdog.IsCompleted;
                    timedOut = !leaseRetired && completed == timeoutSignal;
                    await CancelExecutionAsync(leaseRetired ? "the conservative lease deadline"
                        : timedOut ? "the configured execution timeout" : "worker shutdown");
                    break;
                }

                if (completed == renewalDeadline)
                {
                    renewalDeadline = never;
                    logger.LogWarning(
                        "Execution {InstanceId} renewal exceeded its I/O deadline; lease supervision remains active. " +
                        "ReasonCode={ReasonCode}, RemainingLeaseRunway={RemainingLeaseRunway}",
                        lease.Execution.InstanceId, "StoreIoTimedOut",
                        stopAfter + _options.ExecutionCancellationGracePeriod - timeProvider.GetElapsedTime(grantStarted));
                    renewalDrain ??= DrainStoreOperationAsync(renewal!, lease, "renewal", cancel: true);
                    continue;
                }

                if (renewal is null)
                {
                    renewalStarted = timeProvider.GetTimestamp();
                    renewal = StartStoreOperation(
                        token => store.RenewLeaseAsync(lease.LeaseKey, _options.ExecutionLeaseDuration, token),
                        _options.ExecutionLeaseRenewalTimeout,
                        lease.Execution.InstanceId,
                        "renewal");
                    renewalDeadline = renewal.Deadline;
                    continue;
                }

                var finishedRenewal = renewal;
                finishedRenewal.StopDeadline();
                JobLeaseRenewalResult? renewalResult = null;
                try
                {
                    renewalResult = await finishedRenewal.Operation;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Execution {InstanceId} lease renewal failed; the independent watchdog remains active",
                        lease.Execution.InstanceId);
                }
                finally
                {
                    await (renewalDrain ?? DrainStoreOperationAsync(finishedRenewal, lease, "renewal", cancel: false));
                    renewal = null;
                    renewalDrain = null;
                    renewalDeadline = never;
                    renewalDelay = Task.Delay(_options.ExecutionLeaseRenewInterval, timeProvider, timers.Token);
                }

                if (finishedRenewal.HasTimedOut || renewalResult is null)
                {
                    continue;
                }

                if (watchdog.IsCompleted)
                {
                    leaseRetired = true;
                    await CancelExecutionAsync("the conservative lease deadline");
                    break;
                }

                if (renewalResult.Status == JobLeaseRenewalStatus.Lost)
                {
                    leaseRetired = true;
                    await CancelExecutionAsync("execution lease loss");
                    break;
                }

                if (renewalResult.GrantedAtUtc is not { } grantedAtUtc)
                {
                    logger.LogWarning(
                        "Execution {InstanceId} renewal omitted its authoritative grant time",
                        lease.Execution.InstanceId);
                    continue;
                }

                grantStarted = renewalStarted;
                stopAfter = GetStopAfter(grantedAtUtc, renewalResult.LeaseExpiresAtUtc);
                remaining = stopAfter - timeProvider.GetElapsedTime(grantStarted);
                if (remaining <= TimeSpan.Zero)
                {
                    leaseRetired = true;
                    await CancelExecutionAsync("an expired renewal response");
                    break;
                }

                // Replace and dispose the previous timer rather than retaining a timer for every renewal.
                watchdogCancellation.Cancel();
                watchdogCancellation.Dispose();
                watchdogCancellation = new CancellationTokenSource();
                watchdog = Task.Delay(remaining, timeProvider, watchdogCancellation.Token);
                if (renewalResult.Status == JobLeaseRenewalStatus.CancellationRequested)
                {
                    cancellationRequested = true;
                    await CancelExecutionAsync("a durable cancellation request");
                    break;
                }
            }
        }
        catch
        {
            await CancelExecutionAsync("a worker execution-loop failure");
            throw;
        }
        finally
        {
            watchdogCancellation.Cancel();
            watchdogCancellation.Dispose();
            await timers.CancelAsync();
            if (renewal is not null)
            {
                await (renewalDrain ?? DrainStoreOperationAsync(renewal, lease, "renewal", cancel: true));
            }
        }

        var result = await ObserveExecutionTaskAsync(
            executionTask, lease.Execution.InstanceId, timedOut, cancellationRequested, workerToken);
        if (leaseRetired || Volatile.Read(ref _admissionsStopped) != 0
                         || timeProvider.GetElapsedTime(grantStarted) >= stopAfter + _options.ExecutionCancellationGracePeriod)
        {
            return;
        }

        var shutdownCancelledExecution = workerToken.IsCancellationRequested
                                         && !timedOut && !cancellationRequested
                                         && result.Outcome == JobAttemptOutcome.Cancelled;
        await FinalizeAsync(lease, shutdownCancelledExecution ? null : new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey,
            Outcome = timedOut ? JobAttemptOutcome.Failed
                : cancellationRequested ? JobAttemptOutcome.Cancelled : result.Outcome,
            Message = timedOut ? $"Execution timed out after {lease.Execution.Template.MaxExecutionTimeout}" : result.Message,
            RetryDelay = _options.ExecutionRetryDelay
        });
    }

    private TimeSpan GetStopAfter(DateTimeOffset grantedAtUtc, DateTimeOffset? expiresAtUtc)
    {
        if (expiresAtUtc is null || expiresAtUtc <= grantedAtUtc)
        {
            return TimeSpan.Zero;
        }

        return expiresAtUtc.Value - grantedAtUtc - _options.ExecutionLeaseSafetyMargin
               - _options.ExecutionCancellationGracePeriod;
    }

    private TimeSpan GetCancellationGrace(long grantStarted, TimeSpan stopAfter)
    {
        var remaining = stopAfter + _options.ExecutionCancellationGracePeriod
                        - timeProvider.GetElapsedTime(grantStarted);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero
            : remaining < _options.ExecutionCancellationGracePeriod ? remaining
            : _options.ExecutionCancellationGracePeriod;
    }

    private StoreOperation<T> StartStoreOperation<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        string instanceId,
        string operationName) => new(
        operation,
        timeout,
        timeProvider,
        callbacks => _ = ObserveDetachedAsync(callbacks, instanceId, $"{operationName} cancellation"));

    private async Task DrainStoreOperationAsync<T>(
        StoreOperation<T> operation,
        JobExecutionLease lease,
        string operationName,
        bool cancel)
    {
        operation.StopDeadline();
        if (cancel)
        {
            _ = operation.CancelAsync();
        }

        var completion = operation.Completion;
        if (completion.IsCompleted && !operation.HasTimedOut)
        {
            await ObserveDetachedAsync(completion, lease.Execution.InstanceId, operationName, operation);
            return;
        }

        var timedOut = operation.HasTimedOut;
        lock (_workerStateGate)
        {
            _pendingStoreDrains++;
            _storeRecoveryStarted = null;
            _storeRecoveryRequiresBackoff |= timedOut;
            if (Volatile.Read(ref _admissionsStopped) == 0)
            {
                runtimeState.SetWorkerAdmissionPaused(true);
                _ = runtimeState.SetWorker(false, "Worker admission is paused while cancelled store I/O drains.");
                RecordState("Worker admission paused for store cleanup", HostedServiceState.Degraded,
                    logLevel: timedOut ? LogLevel.Warning : LogLevel.Debug);
            }
        }

        logger.Log(timedOut ? LogLevel.Warning : LogLevel.Debug,
            "Execution {InstanceId} is draining {Operation}; new claims are paused. " +
            "ReasonCode={ReasonCode}, JobKey={JobKey}, ExecutionAttempt={ExecutionAttempt}, DrainTimeout={DrainTimeout}, TimedOut={TimedOut}",
            lease.Execution.InstanceId, operationName, "StoreIoDrainStarted", lease.Execution.Template.JobKey,
            lease.Execution.ExecutionAttempt, _options.ExecutionStoreDrainTimeout, timedOut);
        _metrics?.Record("StoreIoDrainStarted");
        try
        {
            // The clock starts at the first cancellation, even when user-code shutdown delays this observer.
            var remaining = _options.ExecutionStoreDrainTimeout - operation.CancellationElapsed;
            if (!completion.IsCompleted)
            {
                try
                {
                    await completion.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, timeProvider);
                }
                catch (TimeoutException)
                {
                    // Check completion again: the store itself may have thrown TimeoutException.
                }
                catch (Exception)
                {
                    // The observer below records the full I/O and callback failure.
                }
            }

            if (!completion.IsCompleted)
            {
                RequestWorkerRestart(lease.Execution.InstanceId,
                    $"Store {operationName} I/O or its cancellation callbacks exceeded the cancellation drain budget.",
                    "StoreIoDrainExpired");
            }

            // Keep the slot and operation resources owned until actual exit, including after a termination request.
            await ObserveDetachedAsync(completion, lease.Execution.InstanceId, operationName);
            _metrics?.Drained(operationName, operation.CancellationElapsed);
            logger.Log(timedOut ? LogLevel.Information : LogLevel.Debug,
                "Execution {InstanceId} {Operation} exited after cancellation. ReasonCode={ReasonCode}, CancellationElapsed={CancellationElapsed}",
                lease.Execution.InstanceId, operationName, "StoreIoDrained", operation.CancellationElapsed);
        }
        finally
        {
            operation.Dispose();
            lock (_workerStateGate)
            {
                if (--_pendingStoreDrains == 0)
                {
                    _storeRecoveryStarted = timeProvider.GetTimestamp();
                }
            }
            _ = _completionWake.Writer.TryWrite(true);
        }
    }

    private async Task FinalizeAsync(JobExecutionLease lease, JobAttemptCompletion? completion)
    {
        var operation = StartStoreOperation(
            token => completion is null
                ? store.ReleaseLeaseAsync(lease.LeaseKey, token)
                : store.CompleteAttemptAsync(completion, token),
            _options.ExecutionFinalizationTimeout,
            lease.Execution.InstanceId,
            "finalization");
        try
        {
            await Task.WhenAny(operation.Completion, operation.Deadline);
            if (operation.HasTimedOut)
            {
                logger.LogWarning(
                    "Execution {InstanceId} finalization exceeded its I/O deadline; the commit outcome is not replayed. ReasonCode={ReasonCode}",
                    lease.Execution.InstanceId, "FinalizationOutcomeUnknown");
                return;
            }

            var result = await operation.Operation;
            WarnDiscardedOutcome(result.Status, lease.Execution.InstanceId,
                completion is null ? "shutdown release" : "attempt outcome");
        }
        finally
        {
            await DrainStoreOperationAsync(operation, lease, "finalization", cancel: operation.HasTimedOut);
        }
    }

    private bool CanAdmitWork()
    {
        lock (_workerStateGate)
        {
            return Volatile.Read(ref _admissionsStopped) == 0 && _pendingStoreDrains == 0
                && (_storeRecoveryStarted is not { } started
                    || !_storeRecoveryRequiresBackoff
                    || timeProvider.GetElapsedTime(started) >= _options.ExecutionStoreRecoveryDelay);
        }
    }

    private async Task ObserveDetachedAsync(Task task, string instanceId, string operation, IDisposable? ownedResource = null)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Execution {InstanceId} {Operation} exited with an error", instanceId, operation);
        }
        finally
        {
            ownedResource?.Dispose();
        }
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

    private async Task CancelAndAwaitExecutionAsync(
        CancellationTokenSource executionCancellation,
        Task executionTask,
        string instanceId,
        string cancellationReason,
        TimeSpan gracePeriod)
    {
        // Cancellation callbacks are user code too. Their runtime belongs inside the same grace period.
        using var deadlineCancellation = new CancellationTokenSource();
        var deadline = Task.Delay(gracePeriod, timeProvider, deadlineCancellation.Token);
        var callbacks = executionCancellation.CancelAsync();
        var exited = Task.WhenAll(executionTask, callbacks);
        if (await Task.WhenAny(exited, deadline) != exited)
        {
            RequestWorkerRestart(instanceId, $"Job code or cancellation callbacks ignored {cancellationReason}.");
        }

        // Keep the slot occupied until both user code and callbacks really exit, even after requesting shutdown.
        // StopApplication is a supervisor request; it does not itself terminate an uncooperative process.
        try
        {
            await exited;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Execution {InstanceId} faulted while exiting after cancellation", instanceId);
        }
        finally
        {
            await deadlineCancellation.CancelAsync();
        }
    }

    private void RequestWorkerRestart(string instanceId, string reason, string reasonCode = "UnsafeExecution")
    {
        lock (_workerStateGate)
        {
            if (Interlocked.Exchange(ref _admissionsStopped, 1) != 0)
            {
                return;
            }

            var message = $"Worker restart requested because execution '{instanceId}' did not stop safely. {reason}";
            runtimeState.SetWorkerAdmissionPaused(false);
            _ = runtimeState.SetWorker(false, message);
            RecordState(message, HostedServiceState.Faulted, logLevel: LogLevel.Critical);
            logger.LogCritical("Execution {InstanceId} requested worker termination. ReasonCode={ReasonCode}",
                instanceId, reasonCode);
            _metrics?.Record(reasonCode);
        }

        _ = ObserveDetachedAsync(_workerStop.CancelAsync(), instanceId, "worker shutdown cancellation");
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

    private async Task NotifyExecutionCompletedAsync(Task execution)
    {
        try
        {
            await execution;
        }
        finally
        {
            _ = _completionWake.Writer.TryWrite(true);
        }
    }

    private sealed record LocalOwner(string Key, IReadOnlyCollection<string> JobKeys, string ManifestHash);

    /// <summary>
    /// Owns one store call and an independent deadline; cancellation callbacks never run on its timer thread.
    /// </summary>
    private sealed class StoreOperation<T> : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource _deadline = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ITimer _deadlineTimer;
        private readonly Action<Task> _observeCancellation;
        private readonly TimeProvider _clock;
        private Task _callbacks = Task.CompletedTask;
        private long? _cancellationStartedAt;
        private bool _cancellationStarted;
        private bool _timerStopped;
        private bool _timedOut;
        private bool _disposed;
        private int _operationCompleted;

        public StoreOperation(
            Func<CancellationToken, Task<T>> operation,
            TimeSpan timeout,
            TimeProvider clock,
            Action<Task> observeCancellation)
        {
            _observeCancellation = observeCancellation;
            _clock = clock;
            _deadlineTimer = clock.CreateTimer(OnDeadline, null, timeout, Timeout.InfiniteTimeSpan);
            var token = _cancellation.Token;
            Operation = Task.Run(async () =>
            {
                try
                {
                    return await operation(token);
                }
                finally
                {
                    Volatile.Write(ref _operationCompleted, 1);
                }
            }, token);
            Completion = CompleteAsync();
        }

        public Task<T> Operation { get; }

        public Task Deadline => _deadline.Task;

        public bool HasTimedOut
        {
            get
            {
                lock (_gate)
                {
                    return _timedOut;
                }
            }
        }

        public Task Completion { get; }

        public TimeSpan CancellationElapsed
        {
            get
            {
                lock (_gate)
                {
                    return _cancellationStartedAt is { } started ? _clock.GetElapsedTime(started) : TimeSpan.Zero;
                }
            }
        }

        private async Task CompleteAsync()
        {
            try
            {
                await Operation;
            }
            catch (Exception)
            {
                // Join both outcomes below so completion includes cancellation callbacks and the I/O failure.
            }

            Task callbacks;
            lock (_gate)
            {
                _timerStopped = true;
                _deadlineTimer.Dispose();
                callbacks = _callbacks;
            }
            await Task.WhenAll(Operation, callbacks);
        }

        public Task CancelAsync()
        {
            Task callbacks;
            bool started;
            lock (_gate)
            {
                callbacks = CancelLocked(out started);
            }

            if (started)
            {
                _observeCancellation(callbacks);
            }

            return callbacks;
        }

        public void StopDeadline()
        {
            lock (_gate)
            {
                _timerStopped = true;
                _deadlineTimer.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timerStopped = true;
                _deadlineTimer.Dispose();
                _cancellation.Dispose();
            }
        }

        private void OnDeadline(object? state)
        {
            Task callbacks;
            bool started;
            lock (_gate)
            {
                if (_timerStopped || _disposed || Volatile.Read(ref _operationCompleted) != 0)
                {
                    return;
                }

                _timedOut = true;
                callbacks = CancelLocked(out started);
                _deadline.TrySetResult();
            }

            if (started)
            {
                _observeCancellation(callbacks);
            }
        }

        private Task CancelLocked(out bool started)
        {
            started = !_cancellationStarted && !_disposed && Volatile.Read(ref _operationCompleted) == 0;
            if (started)
            {
                _cancellationStarted = true;
                _cancellationStartedAt = _clock.GetTimestamp();
                _callbacks = _cancellation.CancelAsync();
            }

            return _callbacks;
        }
    }
}
