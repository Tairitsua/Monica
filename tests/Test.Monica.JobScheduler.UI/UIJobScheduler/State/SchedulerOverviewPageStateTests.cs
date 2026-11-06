using Monica.Core.Results;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class SchedulerOverviewPageStateTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshAsync_WhenCallsOverlap_ShouldJoinOneAuthorizedLoad()
    {
        var result = NewCompletion<Result<JobSchedulerOverview>>();
        var snapshot = Snapshot();
        var clock = new ControlledTimeProvider();
        var authorizationCalls = 0;
        var loadCalls = 0;
        var state = new SchedulerOverviewPageState(_ =>
        {
            loadCalls++;
            return result.Task;
        }, _ =>
        {
            authorizationCalls++;
            return Task.FromResult(true);
        }, clock, TimeSpan.Zero);

        try
        {
            var first = state.RefreshAsync();
            var second = state.RefreshAsync();
            Assert.Same(first, second);
            Assert.Equal(1, authorizationCalls);
            Assert.Equal(1, loadCalls);
            Assert.True(state.IsLoading);
            Assert.Null(state.Overview);

            result.TrySetResult(Result.Success(snapshot));
            await ObserveAsync(first);
            Assert.Same(snapshot, state.Overview);
            Assert.Equal(InitialTime, state.LastRefreshedUtc);
            Assert.True(state.AccessChecked);
            Assert.True(state.IsAuthorized);
            Assert.False(state.IsLoading);
        }
        finally
        {
            result.TrySetResult(Result.Success(snapshot));
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenLaterQueryFails_ShouldRetainAcceptedSnapshotAndTimestamp()
    {
        var snapshot = Snapshot();
        var next = Result.Success(snapshot);
        var clock = new ControlledTimeProvider();
        var state = new SchedulerOverviewPageState(_ => Task.FromResult(next),
            _ => Task.FromResult(true), clock, TimeSpan.Zero);

        try
        {
            await ObserveAsync(state.RefreshAsync());
            var acceptedAt = state.LastRefreshedUtc;
            clock.UtcNow = InitialTime.AddMinutes(5);
            next = Result.Failure<JobSchedulerOverview>("The durable store query failed.", ResultStatus.BadRequest);

            await ObserveAsync(state.RefreshAsync());

            Assert.Same(snapshot, state.Overview);
            Assert.Equal(acceptedAt, state.LastRefreshedUtc);
            Assert.Equal("The durable store query failed.", state.Error);
            Assert.False(state.IsLoading);
        }
        finally { await ObserveAsync(state.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task RefreshAsync_WhenAuthorizationChanges_ShouldRecheckBeforeReadingAndClearDeniedEvidence()
    {
        var authorized = true;
        var authorizationCalls = 0;
        var loadCalls = 0;
        var state = new SchedulerOverviewPageState(_ =>
        {
            loadCalls++;
            return Task.FromResult(Result.Success(Snapshot()));
        }, _ =>
        {
            authorizationCalls++;
            return Task.FromResult(authorized);
        }, new ControlledTimeProvider(), TimeSpan.Zero);

        try
        {
            await ObserveAsync(state.RefreshAsync());
            Assert.NotNull(state.Overview);
            authorized = false;

            await ObserveAsync(state.RefreshAsync());

            Assert.Equal(2, authorizationCalls);
            Assert.Equal(1, loadCalls);
            Assert.True(state.AccessChecked);
            Assert.False(state.IsAuthorized);
            Assert.Null(state.Overview);
            Assert.Null(state.Error);
        }
        finally { await ObserveAsync(state.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task DisposeAsync_WhenAuthorizationIgnoresCancellation_ShouldDrainInitializationAndRejectNewWork()
    {
        var authorization = NewCompletion<bool>();
        var loadCalls = 0;
        var clock = new ControlledTimeProvider();
        var authorizationToken = CancellationToken.None;
        var state = new SchedulerOverviewPageState(_ =>
        {
            loadCalls++;
            return Task.FromResult(Result.Success(Snapshot()));
        }, token =>
        {
            authorizationToken = token;
            return authorization.Task;
        }, clock, TimeSpan.FromSeconds(5));

        try
        {
            var initialization = state.InitializeAsync();
            Assert.Same(initialization, state.InitializeAsync());
            var refresh = state.RefreshAsync();
            var retirement = state.DisposeAsync().AsTask();
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.True(authorizationToken.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);

            await ObserveAsync(state.RefreshAsync());
            await ObserveAsync(state.InitializeAsync());
            authorization.TrySetResult(true);
            await ObserveAsync(Task.WhenAll(initialization, refresh, retirement));
            await ObserveAsync(state.RefreshAsync());
            await ObserveAsync(state.InitializeAsync());

            Assert.Equal(0, loadCalls);
            Assert.Equal(0, clock.CreateCalls);
            Assert.False(state.AccessChecked);
            Assert.Null(state.Overview);
            Assert.Null(state.LastRefreshedUtc);
        }
        finally
        {
            authorization.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenLoadIgnoresCancellation_ShouldRejectLateEvidenceAndRetainPriorSnapshot()
    {
        var accepted = Snapshot();
        var late = Snapshot(InitialTime.AddMinutes(5));
        var pending = NewCompletion<Result<JobSchedulerOverview>>();
        var clock = new ControlledTimeProvider();
        var loadCalls = 0;
        var loadToken = CancellationToken.None;
        var callbacksAfterRetirement = 0;
        var retirementRequested = false;
        var state = new SchedulerOverviewPageState(token =>
        {
            loadToken = token;
            loadCalls++;
            return loadCalls == 1 ? Task.FromResult(Result.Success(accepted)) : pending.Task;
        }, _ => Task.FromResult(true), clock, TimeSpan.Zero);
        state.Changed += () =>
        {
            if (retirementRequested) callbacksAfterRetirement++;
            return Task.CompletedTask;
        };

        try
        {
            await ObserveAsync(state.RefreshAsync());
            var acceptedAt = state.LastRefreshedUtc;
            var refresh = state.RefreshAsync();
            retirementRequested = true;
            var retirement = state.DisposeAsync().AsTask();
            Assert.True(loadToken.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);

            clock.UtcNow = InitialTime.AddMinutes(5);
            pending.TrySetResult(Result.Success(late));
            await ObserveAsync(Task.WhenAll(refresh, retirement));

            Assert.Same(accepted, state.Overview);
            Assert.Equal(acceptedAt, state.LastRefreshedUtc);
            Assert.Equal(0, callbacksAfterRetirement);
            Assert.False(state.IsLoading);
            await ObserveAsync(state.RefreshAsync());
            Assert.Equal(2, loadCalls);
        }
        finally
        {
            pending.TrySetResult(Result.Success(late));
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InitializeAsync_WhenAutomaticIntervalIsNonpositive_ShouldInitializeOnlyOnceWithoutTimer(int seconds)
    {
        var clock = new ControlledTimeProvider();
        var loadCalls = 0;
        var state = new SchedulerOverviewPageState(_ =>
        {
            loadCalls++;
            return Task.FromResult(Result.Success(Snapshot()));
        }, _ => Task.FromResult(true), clock, TimeSpan.FromSeconds(seconds));

        try
        {
            var initialization = state.InitializeAsync();
            await ObserveAsync(initialization);
            Assert.Same(initialization, state.InitializeAsync());
            Assert.Equal(1, loadCalls);
            Assert.Equal(0, clock.CreateCalls);
        }
        finally { await ObserveAsync(state.DisposeAsync().AsTask()); }
    }

    [Fact]
    public async Task InitializeAsync_WhenTimerTicks_ShouldShareTheAutomaticLoadAndRetireOneLoop()
    {
        var clock = new ControlledTimeProvider();
        var automaticLoadEntered = NewSignal();
        var automaticResult = NewCompletion<Result<JobSchedulerOverview>>();
        var nextSnapshot = Snapshot(InitialTime.AddMinutes(1));
        var loadCalls = 0;
        var state = new SchedulerOverviewPageState(_ =>
        {
            loadCalls++;
            if (loadCalls == 1) return Task.FromResult(Result.Success(Snapshot()));
            automaticLoadEntered.TrySetResult();
            return automaticResult.Task;
        }, _ => Task.FromResult(true), clock, TimeSpan.FromSeconds(5));

        try
        {
            var initialization = state.InitializeAsync();
            await ObserveAsync(initialization);
            Assert.Same(initialization, state.InitializeAsync());
            Assert.Equal(1, clock.CreateCalls);
            var timer = Assert.Single(clock.Timers);

            timer.Fire();
            await ObserveAsync(automaticLoadEntered.Task);
            var joined = state.RefreshAsync();
            Assert.Equal(2, loadCalls);
            automaticResult.TrySetResult(Result.Success(nextSnapshot));
            await ObserveAsync(joined);
            Assert.Same(nextSnapshot, state.Overview);

            var retirement = state.DisposeAsync().AsTask();
            await ObserveAsync(retirement);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.True(timer.IsDisposed);
            timer.Fire();
            Assert.Equal(2, loadCalls);
        }
        finally
        {
            automaticResult.TrySetResult(Result.Success(nextSnapshot));
            await ObserveAsync(state.DisposeAsync().AsTask());
            clock.DisposeTimers();
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenTimerCreationIsInProgress_ShouldDrainAndCloseTheLateTimerOutsideAdmissionGate()
    {
        using var releaseCreation = new ManualResetEventSlim();
        var creationEntered = NewSignal();
        var retirementEntered = NewCompletion<Task>();
        var clock = new ControlledTimeProvider
        {
            OnCreate = () =>
            {
                creationEntered.TrySetResult();
                if (!releaseCreation.Wait(ObservationTimeout, TestContext.Current.CancellationToken))
                    throw new TimeoutException("The controlled timer constructor was not released.");
            }
        };
        var state = new SchedulerOverviewPageState(_ => Task.FromResult(Result.Success(Snapshot())),
            _ => Task.FromResult(true), clock, TimeSpan.FromSeconds(5));
        var initializing = Task.Run(() => state.InitializeAsync(), TestContext.Current.CancellationToken);
        Task retiring = Task.CompletedTask;

        try
        {
            await ObserveAsync(creationEntered.Task);
            retiring = Task.Run(() =>
            {
                var retirement = state.DisposeAsync().AsTask();
                retirementEntered.TrySetResult(retirement);
                return retirement;
            }, TestContext.Current.CancellationToken);
            var retirement = await retirementEntered.Task.WaitAsync(
                ObservationTimeout, TestContext.Current.CancellationToken);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);

            releaseCreation.Set();
            await ObserveAsync(Task.WhenAll(initializing, retiring));
            Assert.Equal(1, clock.CreateCalls);
            Assert.True(Assert.Single(clock.Timers).IsDisposed);
        }
        finally
        {
            releaseCreation.Set();
            try { await ObserveAsync(Task.WhenAll(initializing, retiring, state.DisposeAsync().AsTask())); }
            finally { clock.DisposeTimers(); }
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenDelegatesCoordinateWithAnotherAdmission_ShouldNotHoldTheOwnerGate()
    {
        SchedulerOverviewPageState? state = null;
        var readers = new List<Task>();
        var observations = new List<(bool Retired, Task<Task> Completion)>();
        var signals = new List<ManualResetEventSlim>();
        CancellationTokenRegistration cancellationRegistration = default;

        void CoordinateWithAdmission(bool retired = false)
        {
            var observed = NewCompletion<Task>();
            var signal = new ManualResetEventSlim();
            observations.Add((retired, observed.Task));
            signals.Add(signal);
            readers.Add(Task.Run(() =>
            {
                try { observed.TrySetResult(state!.RefreshAsync()); }
                catch (Exception exception) { observed.TrySetException(exception); }
                finally { signal.Set(); }
            }, TestContext.Current.CancellationToken));
            if (!signal.Wait(ObservationTimeout, TestContext.Current.CancellationToken))
                throw new TimeoutException("The delegate could not coordinate with another refresh admission.");
        }

        var clock = new ControlledTimeProvider { OnGetUtcNow = () => CoordinateWithAdmission() };
        state = new SchedulerOverviewPageState(token =>
        {
            CoordinateWithAdmission();
            cancellationRegistration = token.Register(() => CoordinateWithAdmission(true));
            return Task.FromResult(Result.Success(Snapshot()));
        }, _ =>
        {
            CoordinateWithAdmission();
            return Task.FromResult(true);
        }, clock, TimeSpan.Zero);
        state.Changed += () =>
        {
            CoordinateWithAdmission();
            return Task.CompletedTask;
        };

        try
        {
            var refresh = state.RefreshAsync();
            await ObserveAsync(refresh);
            await ObserveAsync(state.DisposeAsync().AsTask());
            Assert.Contains(observations, observation => observation.Retired);
            Assert.Contains(observations, observation => !observation.Retired);
            foreach (var observation in observations)
            {
                var admitted = await observation.Completion.WaitAsync(
                    ObservationTimeout, TestContext.Current.CancellationToken);
                if (observation.Retired) Assert.True(admitted.IsCompletedSuccessfully);
                else Assert.Same(refresh, admitted);
            }
        }
        finally
        {
            try { await ObserveAsync(state.DisposeAsync().AsTask()); }
            finally
            {
                try
                {
                    await ObserveAsync(Task.WhenAll(readers));
                    await ObserveAsync(Task.WhenAll(observations.Select(observation => observation.Completion)));
                }
                finally
                {
                    cancellationRegistration.Dispose();
                    foreach (var signal in signals) signal.Dispose();
                }
            }
        }
    }

    private static JobSchedulerOverview Snapshot(DateTimeOffset? capturedAt = null) => new()
    {
        Definitions = [],
        ExecutionStateCounts = new Dictionary<global::Monica.JobScheduler.Models.Execution.JobExecutionState, int>(),
        RecentExecutions = [],
        CapturedAtUtc = capturedAt ?? InitialTime
    };

    private static Task ObserveAsync(Task task) => task.WaitAsync(
        ObservationTimeout, TestContext.Current.CancellationToken);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _utcNow = InitialTime;
        private int _createCalls;

        internal Action? OnCreate { get; init; }
        internal Action? OnGetUtcNow { get; init; }
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal ControlledTimer[] Timers { get { lock (_gate) return _timers.ToArray(); } }
        internal DateTimeOffset UtcNow
        {
            get { lock (_gate) return _utcNow; }
            set { lock (_gate) _utcNow = value; }
        }

        public override DateTimeOffset GetUtcNow()
        {
            OnGetUtcNow?.Invoke();
            return UtcNow;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _createCalls);
            OnCreate?.Invoke();
            var timer = new ControlledTimer(callback, state);
            lock (_gate) _timers.Add(timer);
            return timer;
        }

        internal void DisposeTimers()
        {
            foreach (var timer in Timers) timer.Dispose();
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal void Fire() { if (!IsDisposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
