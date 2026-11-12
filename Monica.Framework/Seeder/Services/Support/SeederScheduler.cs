using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Monica.Core.Extensions;
using Monica.Core.Execution;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Models;
using Monica.Framework.Seeder.Models.Internal;

namespace Monica.Framework.Seeder.Services.Support;

internal sealed class SeederScheduler(
    IServiceScopeFactory serviceScopeFactory,
    SeederGraph graph,
    SeederState state,
    ISeederRetryDelay retryDelay,
    IOptions<Monica.Modules.ModuleSeederOption> options,
    ILogger<SeederScheduler> logger,
    IHostApplicationLifetime? applicationLifetime = null) : ISeederExecutor
{
    private readonly Monica.Modules.ModuleSeederOption _options = options.Value;
    private int _batchRunning;
    private volatile bool _startupFinished;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EnterBatch();
        try
        {
            if (_startupFinished) return;
            var selected = graph.Nodes.Where(node => state.GetStatus(node.SeederType) == SeederStatus.Pending).ToArray();
            await RunBatchAsync(selected, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startupFinished = true;
            Volatile.Write(ref _batchRunning, 0);
        }
    }

    public async Task<SeederStateSnapshot> RunAsync(string seederTypeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seederTypeName);
        cancellationToken.ThrowIfCancellationRequested();
        EnterBatch();
        try
        {
            if (!_startupFinished)
                throw new InvalidOperationException("Automatic seeder startup has not completed. Try again after startup.");
            var target = graph.Nodes.FirstOrDefault(node => node.SeederTypeName == seederTypeName)
                ?? throw new InvalidOperationException($"Seeder '{seederTypeName}' does not belong to this host's graph.");
            var selected = new Dictionary<Type, SeederDescriptor>();
            void Include(SeederDescriptor node)
            {
                if (node.Activation == ActivationMode.Disabled)
                    throw new InvalidOperationException($"Seeder '{node.SeederTypeName}' is disabled in this application's placement.");
                if (!selected.TryAdd(node.SeederType, node)) return;
                foreach (var dependency in node.Dependencies)
                    if (!state.IsSucceeded(dependency)) Include(graph.NodesByType[dependency]);
            }
            Include(target);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, applicationLifetime?.ApplicationStopping ?? CancellationToken.None);
            lifetime.Token.ThrowIfCancellationRequested();
            await RunBatchAsync(selected.Values, lifetime.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return state.GetSnapshot();
        }
        finally
        {
            Volatile.Write(ref _batchRunning, 0);
        }
    }

    private void EnterBatch()
    {
        if (Interlocked.CompareExchange(ref _batchRunning, 1, 0) != 0)
            throw new InvalidOperationException("A seeder batch is already running. Wait for it to finish before starting another.");
    }

    private async Task RunBatchAsync(IEnumerable<SeederDescriptor> selected, CancellationToken cancellationToken)
    {
        var pending = selected.ToDictionary(static node => node.SeederType);
        state.AdmitBatch(pending.Keys);
        state.MarkSchedulerStarted();
        var running = new Dictionary<Type, RunningSeeder>();
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (pending.Count > 0 || running.Count > 0)
        {
            BlockDependents(pending);

            if (!runCancellation.IsCancellationRequested)
            {
                ScheduleReadySeeders(pending, running, runCancellation.Token);
            }

            if (running.Count == 0)
            {
                if (pending.Count == 0)
                {
                    break;
                }

                if (runCancellation.IsCancellationRequested)
                {
                    CancelPending(pending, "Seeder execution was cancelled before it could start.");
                    break;
                }

                throw new InvalidOperationException(
                    "Seeder scheduler reached a deadlock even though the dependency graph passed validation.");
            }

            var finished = await WaitForFinishedSeedersAsync(running).ConfigureAwait(false);
            var failFastTrigger = finished
                .Where(static execution =>
                    execution.Result.Status == SeederStatus.Failed &&
                    execution.Descriptor.FailureBehavior == SeederFailureBehavior.FailFast)
                .OrderBy(static execution => execution.Descriptor.SeederTypeName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (failFastTrigger is not null)
            {
                running.Remove(failFastTrigger.Descriptor.SeederType);
                state.MarkFailFastTriggered(
                    failFastTrigger.Descriptor.SeederType,
                    failFastTrigger.Result.Exception!);
                ApplyResults(
                    finished.Where(execution => execution.Descriptor.SeederType !=
                        failFastTrigger.Descriptor.SeederType).ToArray(),
                    running);
                BlockDependents(pending);
                await AbortAsync(pending, running, runCancellation, failFastTrigger.Descriptor)
                    .ConfigureAwait(false);
                return;
            }

            ApplyResults(finished, running);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CancelPending(pending, "Seeder execution was cancelled before it could start.");
            state.MarkSchedulerCancelled();
            return;
        }

        state.MarkSchedulerCompleted();
    }

    private async Task AbortAsync(
        Dictionary<Type, SeederDescriptor> pending,
        Dictionary<Type, RunningSeeder> running,
        CancellationTokenSource runCancellation,
        SeederDescriptor trigger)
    {
        try
        {
            await runCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Cancellation callbacks are application code. One failing callback must not prevent the scheduler from
            // draining every owned scope and publishing a terminal aborted snapshot.
            logger.LogWarning(
                exception,
                "Seeder fail-fast trigger {SeederTypeName} encountered an error while signalling cancellation.",
                trigger.SeederTypeName);
        }

        // Fail-fast is cooperative: apply each completed result as it drains so live diagnostics retain the
        // individual seeder's terminal timing instead of inheriting the slowest in-flight operation's duration.
        while (running.Count > 0)
        {
            var drained = await WaitForFinishedSeedersAsync(running).ConfigureAwait(false);
            ApplyResults(drained, running);
            BlockDependents(pending);
        }

        CancelPending(
            pending,
            $"Seeder run was aborted by fail-fast seeder '{trigger.SeederTypeName}'.");
        state.MarkSchedulerAborted();
    }

    private void ScheduleReadySeeders(
        Dictionary<Type, SeederDescriptor> pending,
        Dictionary<Type, RunningSeeder> running,
        CancellationToken cancellationToken)
    {
        if (running.Values.Any(static execution => execution.Descriptor.ExecutionMode == SeederExecutionMode.Exclusive))
        {
            return;
        }

        var ready = pending.Values
            .Where(IsReady)
            .OrderBy(static descriptor => descriptor.SeederTypeName, StringComparer.Ordinal)
            .ToArray();

        foreach (var descriptor in ready)
        {
            if (cancellationToken.IsCancellationRequested || HasCompletedFailFastFailure(running))
            {
                return;
            }

            if (running.Count >= _options.MaxConcurrency)
            {
                return;
            }

            if (descriptor.ExecutionMode == SeederExecutionMode.Exclusive)
            {
                // Encountering an exclusive node closes the current concurrent wave. The node starts only after
                // every earlier ready node has completed and no later ready node can pass the barrier.
                if (running.Count != 0)
                {
                    return;
                }

                Start(descriptor, pending, running, cancellationToken);
                return;
            }

            Start(descriptor, pending, running, cancellationToken);
        }
    }

    private static bool HasCompletedFailFastFailure(Dictionary<Type, RunningSeeder> running)
    {
        return running.Values.Any(static execution =>
            execution.Descriptor.FailureBehavior == SeederFailureBehavior.FailFast &&
            execution.Task.IsCompletedSuccessfully &&
            execution.Task.Result.Status == SeederStatus.Failed);
    }

    private bool IsReady(SeederDescriptor descriptor)
    {
        return descriptor.Dependencies.All(dependency => state.GetStatus(dependency) == SeederStatus.Succeeded);
    }

    private void Start(
        SeederDescriptor descriptor,
        Dictionary<Type, SeederDescriptor> pending,
        Dictionary<Type, RunningSeeder> running,
        CancellationToken cancellationToken)
    {
        pending.Remove(descriptor.SeederType);
        running.Add(
            descriptor.SeederType,
            new RunningSeeder(descriptor, ExecuteWithRetryAsync(descriptor, cancellationToken)));
    }

    private static async Task<CompletedSeeder[]> WaitForFinishedSeedersAsync(
        Dictionary<Type, RunningSeeder> running)
    {
        await Task.WhenAny(running.Values.Select(static execution => execution.Task)).ConfigureAwait(false);
        // The completion cohort is the set already terminal when the scheduler observes its first completed task.
        // Stable type-name ordering below makes trigger selection deterministic within that observable cohort.
        return await CollectFinishedSeedersAsync(running).ConfigureAwait(false);
    }

    private static async Task<CompletedSeeder[]> CollectFinishedSeedersAsync(
        Dictionary<Type, RunningSeeder> running)
    {
        var finished = running.Values
            .Where(static execution => execution.Task.IsCompleted)
            .OrderBy(static execution => execution.Descriptor.SeederTypeName, StringComparer.Ordinal)
            .ToArray();
        var completed = new CompletedSeeder[finished.Length];
        for (var index = 0; index < finished.Length; index++)
        {
            completed[index] = new CompletedSeeder(
                finished[index].Descriptor,
                await finished[index].Task.ConfigureAwait(false));
        }

        return completed;
    }

    private void ApplyResults(
        IReadOnlyCollection<CompletedSeeder> completed,
        Dictionary<Type, RunningSeeder> running)
    {
        foreach (var execution in completed)
        {
            running.Remove(execution.Descriptor.SeederType);
            switch (execution.Result.Status)
            {
                case SeederStatus.Succeeded:
                    state.MarkSucceeded(execution.Descriptor.SeederType);
                    break;
                case SeederStatus.Failed:
                    state.MarkFailed(execution.Descriptor.SeederType, execution.Result.Exception!);
                    break;
                case SeederStatus.Cancelled:
                    if (execution.Result.Exception is OperationCanceledException cancellationException)
                    {
                        state.MarkCancelled(execution.Descriptor.SeederType, cancellationException);
                    }
                    else
                    {
                        state.MarkCancelled(
                            execution.Descriptor.SeederType,
                            execution.Result.Exception?.GetMessageRecursively() ?? "Seeder execution was cancelled.");
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected terminal seeder status '{execution.Result.Status}'.");
            }
        }
    }

    private async Task<SeederExecutionResult> ExecuteWithRetryAsync(
        SeederDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= descriptor.MaxAttempts; attempt++)
        {
            state.MarkRunning(descriptor.SeederType, attempt);
            try
            {
                await ExecuteAttemptAsync(descriptor.SeederType, cancellationToken).ConfigureAwait(false);
                state.MarkAttemptSucceeded(descriptor.SeederType);
                return SeederExecutionResult.Succeeded;
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                state.MarkAttemptCancelled(descriptor.SeederType, exception);
                return new SeederExecutionResult(SeederStatus.Cancelled, exception);
            }
            catch (Exception exception)
            {
                state.MarkAttemptFailed(descriptor.SeederType, exception);
                if (attempt == descriptor.MaxAttempts)
                {
                    logger.LogError(
                        exception,
                        "Seeder {SeederTypeName} failed terminal attempt {Attempt} of {MaxAttempts} with behavior {FailureBehavior}.",
                        descriptor.SeederTypeName,
                        attempt,
                        descriptor.MaxAttempts,
                        descriptor.FailureBehavior);
                    return new SeederExecutionResult(SeederStatus.Failed, exception);
                }

                logger.LogWarning(
                    exception,
                    "Seeder {SeederTypeName} failed attempt {Attempt} of {MaxAttempts}; retrying.",
                    descriptor.SeederTypeName,
                    attempt,
                    descriptor.MaxAttempts);
                try
                {
                    await retryDelay.DelayAsync(attempt, _options, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException cancellationException)
                {
                    return new SeederExecutionResult(SeederStatus.Cancelled, cancellationException);
                }
            }
        }

        throw new InvalidOperationException("Seeder retry loop ended without producing a terminal result.");
    }

    private async Task ExecuteAttemptAsync(Type seederType, CancellationToken cancellationToken)
    {
        // Each attempt owns a fresh scope so failed transactions and scoped dependencies never leak into retries.
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var seeder = (ISeeder)scope.ServiceProvider.GetRequiredService(seederType);
        var descriptor = ExecutionDescriptor.ForInterface<ExecutionUnit, ExecutionUnit>(
            SeederExecutionPoints.Run,
            seederType,
            typeof(ISeeder),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);

        await scope.ServiceProvider.GetRequiredService<IExecutionPipeline>()
            .ExecuteAsync(
                descriptor,
                ExecutionUnit.Value,
                seeder,
                () => seeder.SeedAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void BlockDependents(Dictionary<Type, SeederDescriptor> pending)
    {
        // Repeat until stable because blocking one node can immediately make another transitive dependent
        // unschedulable in the same scheduler turn.
        bool changed;
        do
        {
            changed = false;
            foreach (var descriptor in pending.Values
                         .OrderBy(static descriptor => descriptor.SeederTypeName, StringComparer.Ordinal)
                         .ToArray())
            {
                var unsuccessfulDependencies = descriptor.Dependencies
                    .Where(dependency => state.GetStatus(dependency) is
                        SeederStatus.Failed or SeederStatus.Blocked or SeederStatus.Cancelled)
                    .Select(dependency => graph.NodesByType[dependency].SeederTypeName)
                    .ToArray();
                if (unsuccessfulDependencies.Length == 0)
                {
                    continue;
                }

                pending.Remove(descriptor.SeederType);
                state.MarkBlocked(descriptor.SeederType, unsuccessfulDependencies);
                changed = true;
            }
        } while (changed);
    }

    private void CancelPending(Dictionary<Type, SeederDescriptor> pending, string message)
    {
        foreach (var descriptor in pending.Values.OrderBy(static descriptor => descriptor.SeederTypeName, StringComparer.Ordinal))
        {
            state.MarkCancelled(descriptor.SeederType, message);
        }

        pending.Clear();
    }

    private sealed record RunningSeeder(SeederDescriptor Descriptor, Task<SeederExecutionResult> Task);

    private sealed record CompletedSeeder(SeederDescriptor Descriptor, SeederExecutionResult Result);

    private sealed record SeederExecutionResult(SeederStatus Status, Exception? Exception)
    {
        public static SeederExecutionResult Succeeded { get; } = new(SeederStatus.Succeeded, null);
    }
}
