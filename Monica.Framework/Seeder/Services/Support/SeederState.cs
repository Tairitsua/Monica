using System.Collections.Immutable;
using Monica.Core.Extensions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Models;
using Monica.Framework.Seeder.Models.Internal;
using Monica.Tool.Extensions;

namespace Monica.Framework.Seeder.Services.Support;

internal sealed class SeederState(SeederGraph graph, TimeProvider timeProvider) : ISeederState
{
    private const int MAX_ERROR_TYPE_LENGTH = 512;
    private const int MAX_ERROR_MESSAGE_LENGTH = 2048;
    private readonly object _sync = new();
    private readonly Dictionary<Type, MutableSeederState> _seeders = CreateInitialStates(graph);
    private SeederRunStatus _status = SeederRunStatus.Waiting;
    private DateTimeOffset? _startedAtUtc;
    private DateTimeOffset? _completedAtUtc;
    private long? _startedTimestamp;
    private long? _completedTimestamp;
    private string? _failFastTriggerSeederTypeName;

    public SeederStateSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var capturedAtUtc = timeProvider.GetUtcNow();
            var capturedTimestamp = timeProvider.GetTimestamp();
            return new SeederStateSnapshot
            {
                CapturedAtUtc = capturedAtUtc,
                Status = _status,
                StartedAtUtc = _startedAtUtc,
                CompletedAtUtc = _completedAtUtc,
                Duration = GetDuration(_startedTimestamp, _completedTimestamp, capturedTimestamp),
                FailFastTriggerSeederTypeName = _failFastTriggerSeederTypeName,
                Seeders = graph.Nodes.Select(descriptor => CreateSnapshot(descriptor, capturedTimestamp)).ToImmutableArray()
            };
        }
    }

    public SeederStatus GetStatus(Type seederType)
    {
        lock (_sync)
        {
            return GetMutableState(seederType).Status;
        }
    }

    public bool IsSucceeded(Type seederType)
    {
        ArgumentNullException.ThrowIfNull(seederType);
        lock (_sync)
        {
            return _seeders.TryGetValue(seederType, out var state) && state.Status == SeederStatus.Succeeded;
        }
    }

    public void AdmitBatch(IEnumerable<Type> seederTypes)
    {
        lock (_sync)
        {
            foreach (var type in seederTypes)
            {
                var state = GetMutableState(type);
                state.Status = SeederStatus.Pending;
                state.ParticipatesInReadiness = true;
                state.ActivationReason = null;
            }
        }
    }

    public void MarkSchedulerStarted()
    {
        lock (_sync)
        {
            _startedAtUtc = timeProvider.GetUtcNow();
            _startedTimestamp = timeProvider.GetTimestamp();
            _completedAtUtc = null;
            _completedTimestamp = null;
            _failFastTriggerSeederTypeName = null;
            _status = SeederRunStatus.Running;
        }
    }

    public void MarkFailFastTriggered(
        Type seederType,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(seederType);
        ArgumentNullException.ThrowIfNull(exception);
        var (errorType, errorMessage) = GetBoundedDiagnostics(exception);
        lock (_sync)
        {
            if (_status == SeederRunStatus.Running)
            {
                // Keep the trigger's terminal evidence and the unhealthy run transition indivisible to snapshots.
                var seeder = GetMutableState(seederType);
                CompleteActiveAttemptIfNecessary(
                    seeder,
                    SeederAttemptStatus.Failed,
                    errorType,
                    errorMessage);
                MarkTerminal(seeder, SeederStatus.Failed, errorType, errorMessage);
                _status = SeederRunStatus.Aborting;
                _failFastTriggerSeederTypeName = graph.NodesByType[seederType].SeederTypeName;
            }
        }
    }

    public void MarkSchedulerAborted()
    {
        lock (_sync)
        {
            if (_failFastTriggerSeederTypeName is null)
            {
                throw new InvalidOperationException("A Seeder run cannot be aborted before a FailFast trigger is recorded.");
            }

            CompleteRun(SeederRunStatus.Aborted);
        }
    }

    public void MarkSchedulerCancelled()
    {
        lock (_sync)
        {
            MarkPendingSeedersCancelled();
            CompleteRun(SeederRunStatus.Cancelled);
        }
    }

    /// <summary>
    /// Transitions a run that never left the waiting phase to <see cref="SeederRunStatus.Cancelled"/>.
    /// Unlike <see cref="MarkSchedulerCancelled"/>, this is a no-op once the run has started or completed,
    /// so shutdown can publish a deterministic cancelled state even when the background loop never ran.
    /// </summary>
    public void MarkSchedulerCancelledIfWaiting()
    {
        lock (_sync)
        {
            if (_status != SeederRunStatus.Waiting)
            {
                return;
            }

            MarkPendingSeedersCancelled();
            CompleteRun(SeederRunStatus.Cancelled);
        }
    }

    private void MarkPendingSeedersCancelled()
    {
        var completedAtUtc = timeProvider.GetUtcNow();
        var completedTimestamp = timeProvider.GetTimestamp();
        foreach (var seeder in _seeders.Values.Where(static seeder => seeder.Status == SeederStatus.Pending))
        {
            seeder.Status = SeederStatus.Cancelled;
            seeder.CompletedAtUtc = completedAtUtc;
            seeder.CompletedTimestamp = completedTimestamp;
            seeder.ErrorType = typeof(OperationCanceledException).GetCleanFullName();
            seeder.ErrorMessage = "Seeder execution was cancelled before it could start.";
        }
    }

    public void MarkSchedulerCompleted()
    {
        lock (_sync)
        {
            var status = _seeders.Values.Any(static seeder => seeder.Status is
                    SeederStatus.Failed or SeederStatus.Blocked or SeederStatus.Cancelled)
                ? SeederRunStatus.CompletedWithFailures
                : _seeders.Values.Any(static seeder => seeder.Status == SeederStatus.Held)
                    ? SeederRunStatus.Held
                    : SeederRunStatus.Succeeded;
            CompleteRun(status);
        }
    }

    public void MarkRunning(Type seederType, int attempt)
    {
        lock (_sync)
        {
            var state = GetMutableState(seederType);
            var startedAtUtc = timeProvider.GetUtcNow();
            var startedTimestamp = timeProvider.GetTimestamp();
            state.Status = SeederStatus.Running;
            state.Attempts++;
            state.StartedAtUtc ??= startedAtUtc;
            state.StartedTimestamp ??= startedTimestamp;
            state.CompletedAtUtc = null;
            state.CompletedTimestamp = null;
            state.ErrorType = null;
            state.ErrorMessage = null;
            state.AttemptHistory.Add(new MutableAttemptState(state.Attempts, startedAtUtc, startedTimestamp));
        }
    }

    public void MarkAttemptSucceeded(Type seederType)
    {
        CompleteAttempt(seederType, SeederAttemptStatus.Succeeded, null, null);
    }

    public void MarkAttemptFailed(Type seederType, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (errorType, errorMessage) = GetBoundedDiagnostics(exception);
        CompleteAttempt(
            seederType,
            SeederAttemptStatus.Failed,
            errorType,
            errorMessage);
    }

    public void MarkAttemptCancelled(Type seederType, OperationCanceledException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (errorType, errorMessage) = GetBoundedDiagnostics(exception);
        CompleteAttempt(
            seederType,
            SeederAttemptStatus.Cancelled,
            errorType,
            errorMessage);
    }

    public void MarkSucceeded(Type seederType)
    {
        TransitionToTerminal(
            seederType,
            SeederStatus.Succeeded,
            SeederAttemptStatus.Succeeded,
            null,
            null);
    }

    public void MarkFailed(Type seederType, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (errorType, errorMessage) = GetBoundedDiagnostics(exception);
        TransitionToTerminal(
            seederType,
            SeederStatus.Failed,
            SeederAttemptStatus.Failed,
            errorType,
            errorMessage);
    }

    public void MarkBlocked(Type seederType, IReadOnlyCollection<string> dependencyNames)
    {
        ArgumentNullException.ThrowIfNull(dependencyNames);
        TransitionToTerminal(
            seederType,
            SeederStatus.Blocked,
            null,
            "SeederDependencyFailure",
            Bound($"Blocked by unsuccessful dependencies: {string.Join(", ", dependencyNames)}.", MAX_ERROR_MESSAGE_LENGTH));
    }

    public void MarkCancelled(Type seederType, string message)
    {
        var errorType = typeof(OperationCanceledException).GetCleanFullName();
        var boundedMessage = Bound(message, MAX_ERROR_MESSAGE_LENGTH);
        TransitionToTerminal(
            seederType,
            SeederStatus.Cancelled,
            SeederAttemptStatus.Cancelled,
            errorType,
            boundedMessage);
    }

    public void MarkCancelled(Type seederType, OperationCanceledException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var (errorType, errorMessage) = GetBoundedDiagnostics(exception);
        TransitionToTerminal(
            seederType,
            SeederStatus.Cancelled,
            SeederAttemptStatus.Cancelled,
            errorType,
            errorMessage);
    }

    private SeederExecutionSnapshot CreateSnapshot(SeederDescriptor descriptor, long capturedTimestamp)
    {
        var state = GetMutableState(descriptor.SeederType);
        return new SeederExecutionSnapshot
        {
            SeederName = descriptor.SeederName,
            SeederTypeName = descriptor.SeederTypeName,
            ExecutionMode = descriptor.ExecutionMode,
            ExecutionModeSource = descriptor.ExecutionModeSource,
            Criticality = descriptor.Criticality,
            CriticalitySource = descriptor.CriticalitySource,
            FailureBehavior = descriptor.FailureBehavior,
            FailureBehaviorSource = descriptor.FailureBehaviorSource,
            Status = state.Status,
            Activation = descriptor.Activation,
            ActivationReason = state.ActivationReason,
            ParticipatesInReadiness = state.ParticipatesInReadiness,
            Attempts = state.Attempts,
            MaxAttempts = descriptor.MaxAttempts,
            MaxAttemptsSource = descriptor.MaxAttemptsSource,
            Dependencies = descriptor.Dependencies
                .Select(dependency => graph.NodesByType[dependency].SeederTypeName)
                .ToImmutableArray(),
            StartedAtUtc = state.StartedAtUtc,
            CompletedAtUtc = state.CompletedAtUtc,
            Duration = GetDuration(state.StartedTimestamp, state.CompletedTimestamp, capturedTimestamp),
            AttemptHistory = state.AttemptHistory
                .Select(attempt => CreateAttemptSnapshot(attempt, capturedTimestamp))
                .ToImmutableArray(),
            ErrorType = state.ErrorType,
            ErrorMessage = state.ErrorMessage
        };
    }

    private SeederAttemptSnapshot CreateAttemptSnapshot(
        MutableAttemptState attempt,
        long capturedTimestamp)
    {
        return new SeederAttemptSnapshot
        {
            AttemptNumber = attempt.AttemptNumber,
            Status = attempt.Status,
            StartedAtUtc = attempt.StartedAtUtc,
            CompletedAtUtc = attempt.CompletedAtUtc,
            Duration = timeProvider.GetElapsedTime(
                attempt.StartedTimestamp,
                attempt.CompletedTimestamp ?? capturedTimestamp),
            ErrorType = attempt.ErrorType,
            ErrorMessage = attempt.ErrorMessage
        };
    }

    private void CompleteAttempt(
        Type seederType,
        SeederAttemptStatus status,
        string? errorType,
        string? errorMessage)
    {
        lock (_sync)
        {
            CompleteActiveAttempt(GetMutableState(seederType), status, errorType, errorMessage);
        }
    }

    private void TransitionToTerminal(
        Type seederType,
        SeederStatus status,
        SeederAttemptStatus? activeAttemptStatus,
        string? errorType,
        string? errorMessage)
    {
        lock (_sync)
        {
            var state = GetMutableState(seederType);
            if (activeAttemptStatus is { } attemptStatus)
            {
                CompleteActiveAttemptIfNecessary(state, attemptStatus, errorType, errorMessage);
            }

            MarkTerminal(state, status, errorType, errorMessage);
        }
    }

    private void CompleteActiveAttemptIfNecessary(
        MutableSeederState state,
        SeederAttemptStatus status,
        string? errorType,
        string? errorMessage)
    {
        if (state.AttemptHistory.Count > 0 && state.AttemptHistory[^1].Status == SeederAttemptStatus.Running)
        {
            CompleteActiveAttempt(state, status, errorType, errorMessage);
        }
    }

    private void CompleteActiveAttempt(
        MutableSeederState state,
        SeederAttemptStatus status,
        string? errorType,
        string? errorMessage)
    {
        if (state.AttemptHistory.Count == 0 || state.AttemptHistory[^1].Status != SeederAttemptStatus.Running)
        {
            throw new InvalidOperationException("No running seeder attempt is available to complete.");
        }

        var attempt = state.AttemptHistory[^1];
        attempt.Status = status;
        attempt.CompletedAtUtc = timeProvider.GetUtcNow();
        attempt.CompletedTimestamp = timeProvider.GetTimestamp();
        attempt.ErrorType = errorType;
        attempt.ErrorMessage = errorMessage;
    }

    private void MarkTerminal(
        MutableSeederState state,
        SeederStatus status,
        string? errorType,
        string? errorMessage)
    {
        var attempt = state.AttemptHistory.LastOrDefault();
        var useAttemptCompletion = attempt is { CompletedAtUtc: not null, CompletedTimestamp: not null } &&
            MatchesTerminalAttempt(status, attempt.Status);
        state.Status = status;
        state.CompletedAtUtc = useAttemptCompletion ? attempt!.CompletedAtUtc : timeProvider.GetUtcNow();
        state.CompletedTimestamp = useAttemptCompletion
            ? attempt!.CompletedTimestamp
            : timeProvider.GetTimestamp();
        state.ErrorType = errorType;
        state.ErrorMessage = errorMessage;
    }

    private void CompleteRun(SeederRunStatus status)
    {
        _status = status;
        if (_completedAtUtc is null)
        {
            _completedAtUtc = timeProvider.GetUtcNow();
            _completedTimestamp = timeProvider.GetTimestamp();
        }
    }

    private MutableSeederState GetMutableState(Type seederType)
    {
        return _seeders.TryGetValue(seederType, out var state)
            ? state
            : throw new InvalidOperationException($"Seeder '{seederType.FullName}' does not belong to this host's graph.");
    }

    private static Dictionary<Type, MutableSeederState> CreateInitialStates(SeederGraph graph)
    {
        var states = new Dictionary<Type, MutableSeederState>();
        MutableSeederState Create(Type type)
        {
            if (states.TryGetValue(type, out var existing)) return existing;
            var node = graph.NodesByType[type];
            var state = new MutableSeederState
            {
                ParticipatesInReadiness = node.Activation == ActivationMode.Automatic,
                Status = node.Activation switch
                {
                    ActivationMode.Disabled => SeederStatus.Disabled,
                    ActivationMode.Manual => SeederStatus.Held,
                    _ => SeederStatus.Pending
                },
                ActivationReason = node.Activation switch
                {
                    ActivationMode.Disabled => "The seeder is unavailable in this application's placement.",
                    ActivationMode.Manual => "Automatic work is disabled; run this seeder explicitly when needed.",
                    _ => null
                }
            };
            states.Add(type, state);
            if (node.Activation == ActivationMode.Automatic)
            {
                if (graph.GetAutomaticBlockReason(type) is { } blocked)
                {
                    state.Status = SeederStatus.Held;
                    state.ActivationReason = blocked;
                }
            }
            return state;
        }

        foreach (var node in graph.Nodes) Create(node.SeederType);
        return states;
    }

    private TimeSpan? GetDuration(
        long? startedTimestamp,
        long? completedTimestamp,
        long capturedTimestamp)
    {
        return startedTimestamp is null
            ? null
            : timeProvider.GetElapsedTime(startedTimestamp.Value, completedTimestamp ?? capturedTimestamp);
    }

    private static bool MatchesTerminalAttempt(SeederStatus status, SeederAttemptStatus attemptStatus)
    {
        return (status, attemptStatus) switch
        {
            (SeederStatus.Succeeded, SeederAttemptStatus.Succeeded) => true,
            (SeederStatus.Failed, SeederAttemptStatus.Failed) => true,
            (SeederStatus.Cancelled, SeederAttemptStatus.Cancelled) => true,
            _ => false
        };
    }

    private static string? Bound(string? value, int maximumLength)
    {
        if (value is null || value.Length <= maximumLength)
        {
            return value;
        }

        return value[..maximumLength];
    }

    private static (string? ErrorType, string? ErrorMessage) GetBoundedDiagnostics(Exception exception)
    {
        return (
            Bound(exception.GetType().GetCleanFullName(), MAX_ERROR_TYPE_LENGTH),
            Bound(exception.GetMessageRecursively(), MAX_ERROR_MESSAGE_LENGTH));
    }

    private sealed class MutableSeederState
    {
        public SeederStatus Status { get; set; }
        public bool ParticipatesInReadiness { get; set; }
        public string? ActivationReason { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public long? StartedTimestamp { get; set; }
        public long? CompletedTimestamp { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
        public List<MutableAttemptState> AttemptHistory { get; } = [];
    }

    private sealed class MutableAttemptState(
        int attemptNumber,
        DateTimeOffset startedAtUtc,
        long startedTimestamp)
    {
        public int AttemptNumber { get; } = attemptNumber;
        public SeederAttemptStatus Status { get; set; } = SeederAttemptStatus.Running;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public long StartedTimestamp { get; } = startedTimestamp;
        public long? CompletedTimestamp { get; set; }
        public string? ErrorType { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
