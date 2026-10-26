using Monica.Core.Results;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.UIJobScheduler.Abstractions;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Xunit;

namespace Test.Monica.JobScheduler.UI.UIJobScheduler.State;

public sealed class SchedulerRuntimePageStateTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset InitialTime = new(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshAsync_WhenCallsOverlap_ShouldJoinOneAuthorizedCoreAndRegistryRead()
    {
        var boundary = new RuntimeBoundary();
        var authorization = Signal<bool>();
        boundary.Authorize = _ => authorization.Task;
        var state = boundary.Create();
        try
        {
            var first = state.RefreshAsync();
            var second = state.RefreshAsync();
            Assert.Equal(1, boundary.AuthorizationCalls);
            Assert.Equal(0, boundary.CoreCalls);
            Assert.True(state.IsLoading);
            authorization.TrySetResult(true);
            await ObserveAsync(Task.WhenAll(first, second));

            Assert.Equal(1, boundary.CoreCalls);
            Assert.Equal(1, boundary.Registry.Calls);
            Assert.True(state.HasLoadedSnapshot);
            Assert.True(state.HasLoadedWorkerInsight);
            Assert.False(state.IsLoading);
        }
        finally
        {
            authorization.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_WhenFirstCoreResultFailsOrIsNull_ShouldNotDisplayUnavailableAsEmpty(bool nullSuccess)
    {
        var boundary = new RuntimeBoundary
        {
            Load = _ => Task.FromResult(new Res<JobSchedulerRuntimeOverview>
            {
                Status = nullSuccess ? ResStatus.Ok : ResStatus.BadRequest,
                Message = null,
                Data = null
            })
        };
        await using var state = boundary.Create();
        await ObserveAsync(state.RefreshAsync());

        Assert.True(state.IsQueryFailed);
        Assert.False(state.HasLoadedSnapshot);
        Assert.Null(state.Overview);
        Assert.Null(state.LastRefreshedUtc);
        Assert.Null(state.Error);
        Assert.True(state.HasLoadedWorkerInsight);
        Assert.Empty(state.Workers);
        Assert.Equal(1, boundary.Registry.Calls);

        boundary.Load = _ => Task.FromResult(Res.Ok(Snapshot()));
        await ObserveAsync(state.RefreshAsync());
        Assert.False(state.IsQueryFailed);
        Assert.True(state.HasLoadedSnapshot);
        Assert.Empty(Assert.IsType<JobSchedulerRuntimeOverview>(state.Overview).Owners);
        Assert.Equal(InitialTime, state.LastRefreshedUtc);
    }

    [Fact]
    public async Task RefreshAsync_WhenCoreFails_ShouldRetainItsOwnEvidenceWhileRegistryAdvances()
    {
        var boundary = new RuntimeBoundary();
        await using var state = boundary.Create();
        await ObserveAsync(state.RefreshAsync());
        var prior = state.Overview;
        var coreAccepted = state.LastRefreshedUtc;
        boundary.Clock.UtcNow = InitialTime.AddMinutes(5);
        boundary.Load = _ => Task.FromException<Res<JobSchedulerRuntimeOverview>>(new IOException("Core unavailable"));
        IReadOnlyList<JobSchedulerWorkerInstanceView> nextWorkers = [Worker("new-instance")];
        boundary.Registry.Load = _ => Task.FromResult(nextWorkers);

        await ObserveAsync(state.RefreshAsync());

        Assert.Same(prior, state.Overview);
        Assert.Equal(coreAccepted, state.LastRefreshedUtc);
        Assert.True(state.HasLoadedSnapshot);
        Assert.True(state.IsQueryFailed);
        Assert.Equal("Core unavailable", state.Error);
        Assert.Same(nextWorkers, state.Workers);
        Assert.Equal(boundary.Clock.UtcNow, state.WorkerInsightObservedAtUtc);
        Assert.False(state.IsWorkerInsightFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_WhenFirstRegistryReadFailsOrIsNull_ShouldDistinguishUnavailableFromEmpty(bool nullResult)
    {
        var boundary = new RuntimeBoundary();
        boundary.Registry.Load = _ => nullResult
            ? Task.FromResult<IReadOnlyList<JobSchedulerWorkerInstanceView>>(null!)
            : Task.FromException<IReadOnlyList<JobSchedulerWorkerInstanceView>>(new IOException("Registry unavailable"));
        await using var state = boundary.Create();
        await ObserveAsync(state.RefreshAsync());

        Assert.True(state.HasLoadedSnapshot);
        Assert.False(state.IsQueryFailed);
        Assert.True(state.IsWorkerInsightFailed);
        Assert.False(state.HasLoadedWorkerInsight);
        Assert.Null(state.WorkerInsightObservedAtUtc);
        Assert.Empty(state.Workers);
        Assert.Equal(nullResult ? null : "Registry unavailable", state.WorkerInsightError);

        boundary.Registry.Load = _ => Task.FromResult<IReadOnlyList<JobSchedulerWorkerInstanceView>>([]);
        await ObserveAsync(state.RefreshAsync());
        Assert.True(state.HasLoadedWorkerInsight);
        Assert.False(state.IsWorkerInsightFailed);
        Assert.Empty(state.Workers);
        Assert.Equal(InitialTime, state.WorkerInsightObservedAtUtc);
        Assert.Null(state.WorkerInsightError);
    }

    [Fact]
    public async Task RefreshAsync_WhenRegistryFails_ShouldRetainItsRowsClassificationAndTimeWhileCoreAdvances()
    {
        var boundary = new RuntimeBoundary();
        IReadOnlyList<JobSchedulerWorkerInstanceView> rows = [Worker(" old/上海?instance=one ")];
        boundary.Registry.Load = _ => Task.FromResult(rows);
        await using var state = boundary.Create();
        await ObserveAsync(state.RefreshAsync());
        var registryAccepted = state.WorkerInsightObservedAtUtc;
        var next = Snapshot(InitialTime.AddHours(2));
        boundary.Clock.UtcNow = InitialTime.AddHours(2);
        boundary.Load = _ => Task.FromResult(Res.Ok(next));
        boundary.Registry.Load = _ => Task.FromException<IReadOnlyList<JobSchedulerWorkerInstanceView>>(
            new IOException("Registry unreachable"));

        await ObserveAsync(state.RefreshAsync());

        Assert.Same(next, state.Overview);
        Assert.Equal(boundary.Clock.UtcNow, state.LastRefreshedUtc);
        Assert.Same(rows, state.Workers);
        Assert.Equal(" old/上海?instance=one ", Assert.Single(state.Workers).InstanceId);
        Assert.Equal(JobSchedulerWorkerInstanceStatus.Online, state.Workers[0].Status);
        Assert.Equal(registryAccepted, state.WorkerInsightObservedAtUtc);
        Assert.True(state.HasLoadedWorkerInsight);
        Assert.True(state.IsWorkerInsightFailed);
        Assert.Equal("Registry unreachable", state.WorkerInsightError);
    }

    [Fact]
    public async Task RefreshAsync_WhenProviderIsAbsent_ShouldNotClaimAnAcceptedEmptyRegistry()
    {
        var boundary = new RuntimeBoundary();
        await using var state = boundary.Create(withRegistry: false);
        await ObserveAsync(state.RefreshAsync());

        Assert.True(state.HasLoadedSnapshot);
        Assert.False(state.HasWorkerInsight);
        Assert.False(state.HasLoadedWorkerInsight);
        Assert.False(state.IsWorkerInsightFailed);
        Assert.Null(state.WorkerInsightObservedAtUtc);
        Assert.Equal(0, boundary.Registry.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAsync_WhenAccessIsDeniedOrFaults_ShouldClearBothEvidencePlanesAndSkipProducers(bool faults)
    {
        var boundary = new RuntimeBoundary();
        boundary.Registry.Load = _ => Task.FromResult<IReadOnlyList<JobSchedulerWorkerInstanceView>>([Worker("one")]);
        await using var state = boundary.Create();
        await ObserveAsync(state.RefreshAsync());
        boundary.Authorize = _ => faults
            ? Task.FromException<bool>(new IOException("Access provider unavailable"))
            : Task.FromResult(false);

        await ObserveAsync(state.RefreshAsync());

        Assert.Equal(1, boundary.CoreCalls);
        Assert.Equal(1, boundary.Registry.Calls);
        Assert.False(state.IsAuthorized);
        Assert.Equal(!faults, state.AccessChecked);
        Assert.Equal(faults, state.IsQueryFailed);
        Assert.Equal(faults ? "Access provider unavailable" : null, state.Error);
        Assert.False(state.HasLoadedSnapshot);
        Assert.Null(state.Overview);
        Assert.Null(state.LastRefreshedUtc);
        Assert.False(state.HasLoadedWorkerInsight);
        Assert.Empty(state.Workers);
        Assert.Null(state.WorkerInsightObservedAtUtc);
        Assert.False(state.IsWorkerInsightFailed);
        Assert.Null(state.WorkerInsightError);

        boundary.Authorize = _ => Task.FromResult(true);
        await ObserveAsync(state.RefreshAsync());
        Assert.True(state.AccessChecked);
        Assert.True(state.IsAuthorized);
        Assert.True(state.HasLoadedSnapshot);
        Assert.True(state.HasLoadedWorkerInsight);
        Assert.False(state.IsQueryFailed);
    }

    [Fact]
    public async Task RefreshAsync_WhenLoadingSubscriberReenters_ShouldJoinReadsWithoutCircularNotificationWait()
    {
        var boundary = new RuntimeBoundary();
        var result = Signal<Res<JobSchedulerRuntimeOverview>>();
        boundary.Load = _ => result.Task;
        var state = boundary.Create();
        var joined = Signal<Task>();
        var once = 0;
        state.Changed += async () =>
        {
            if (!state.IsLoading || Interlocked.Exchange(ref once, 1) != 0) return;
            var reentered = state.RefreshAsync();
            joined.TrySetResult(reentered);
            await reentered;
        };

        try
        {
            var refresh = state.RefreshAsync();
            var reentry = await ObserveAsync(joined.Task);
            Assert.Equal(1, boundary.CoreCalls);
            result.TrySetResult(Res.Ok(Snapshot()));
            await ObserveAsync(Task.WhenAll(refresh, reentry));
            Assert.True(state.HasLoadedSnapshot);
            Assert.Equal(1, boundary.AuthorizationCalls);
            Assert.Equal(1, boundary.Registry.Calls);
        }
        finally
        {
            result.TrySetResult(Res.Ok(Snapshot()));
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenFinalSubscriberReenters_ShouldStartANewReadAfterCompletedReadDetaches()
    {
        var boundary = new RuntimeBoundary();
        await using var state = boundary.Create();
        var once = 0;
        state.Changed += async () =>
        {
            if (state.IsLoading || Interlocked.Exchange(ref once, 1) != 0) return;
            await state.RefreshAsync();
        };

        await ObserveAsync(state.RefreshAsync());

        Assert.Equal(2, boundary.AuthorizationCalls);
        Assert.Equal(2, boundary.CoreCalls);
        Assert.Equal(2, boundary.Registry.Calls);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public async Task RefreshAsync_WhenSubscriberFaults_ShouldObserveAllSubscribersAndLeaveNextReadUsable()
    {
        var boundary = new RuntimeBoundary();
        await using var state = boundary.Create();
        var faults = 0;
        var secondSubscriberCalls = 0;
        state.Changed += () => Interlocked.Exchange(ref faults, 1) == 0
            ? Task.FromException(new IOException("Subscriber failed")) : Task.CompletedTask;
        state.Changed += () => { secondSubscriberCalls++; return Task.CompletedTask; };

        var error = await Assert.ThrowsAsync<IOException>(() => ObserveAsync(state.RefreshAsync()));
        Assert.Equal("Subscriber failed", error.Message);
        Assert.True(state.HasLoadedSnapshot);
        Assert.Equal(2, secondSubscriberCalls);

        await ObserveAsync(state.RefreshAsync());
        Assert.Equal(2, boundary.CoreCalls);
        Assert.Equal(4, secondSubscriberCalls);
        Assert.False(state.IsLoading);
    }

    [Fact]
    public async Task RefreshAsync_WhenJoinerReadCompletesBeforeLoadingPublication_ShouldKeepActualNotificationOwned()
    {
        var boundary = new RuntimeBoundary();
        var read = Signal<Res<JobSchedulerRuntimeOverview>>();
        var releaseNotification = Signal();
        boundary.Load = _ => read.Task;
        var state = boundary.Create();
        state.Changed += () => state.IsLoading ? releaseNotification.Task : Task.CompletedTask;
        var first = state.RefreshAsync();
        var joined = state.RefreshAsync();
        Task retirement = Task.CompletedTask;
        try
        {
            read.TrySetResult(Res.Ok(Snapshot()));
            await ObserveAsync(joined);
            Assert.False(first.IsCompleted);
            retirement = state.DisposeAsync().AsTask();
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            releaseNotification.TrySetResult();
            await ObserveAsync(Task.WhenAll(first, retirement));
        }
        finally
        {
            read.TrySetResult(Res.Ok(Snapshot()));
            releaseNotification.TrySetResult();
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenFinalNotificationIsPending_ShouldJoinActualCallbackCompletion()
    {
        var boundary = new RuntimeBoundary();
        var finalEntered = Signal();
        var release = Signal();
        var state = boundary.Create();
        state.Changed += () =>
        {
            if (state.IsLoading) return Task.CompletedTask;
            finalEntered.TrySetResult();
            return release.Task;
        };
        var refresh = state.RefreshAsync();
        try
        {
            await ObserveAsync(finalEntered.Task);
            var retirement = state.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            release.TrySetResult();
            await ObserveAsync(Task.WhenAll(refresh, retirement));
        }
        finally
        {
            release.TrySetResult();
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenAuthorizationIgnoresCancellation_ShouldDrainInitializationAndRejectNewAdmissions()
    {
        var boundary = new RuntimeBoundary();
        var authorization = Signal<bool>();
        boundary.Authorize = _ => authorization.Task;
        var state = boundary.Create(interval: TimeSpan.FromSeconds(5));
        var first = state.InitializeAsync();
        var duplicate = state.InitializeAsync();
        var refresh = state.RefreshAsync();
        try
        {
            var retirement = state.DisposeAsync().AsTask();
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.True(state.IsDisposed);
            Assert.True(boundary.AuthorizationToken.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            await AssertCanceledAsync(state.RefreshAsync());
            await AssertCanceledAsync(state.InitializeAsync());
            authorization.TrySetResult(true);
            await AssertCanceledAsync(first);
            await AssertCanceledAsync(duplicate);
            await AssertCanceledAsync(refresh);
            await ObserveAsync(retirement);
            Assert.Equal(0, boundary.CoreCalls);
            Assert.Equal(0, boundary.Registry.Calls);
            Assert.Equal(0, boundary.Clock.CreateCalls);
            Assert.False(state.AccessChecked);
        }
        finally
        {
            authorization.TrySetResult(true);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WhenProducerIgnoresCancellation_ShouldDrainItAndRejectLateEvidence(bool registry)
    {
        var boundary = new RuntimeBoundary();
        IReadOnlyList<JobSchedulerWorkerInstanceView> acceptedWorkers = [Worker("accepted")];
        boundary.Registry.Load = _ => Task.FromResult(acceptedWorkers);
        var state = boundary.Create();
        await ObserveAsync(state.RefreshAsync());
        var accepted = state.Overview;
        var coreAt = state.LastRefreshedUtc;
        var registryAt = state.WorkerInsightObservedAtUtc;
        var core = Signal<Res<JobSchedulerRuntimeOverview>>();
        var workers = Signal<IReadOnlyList<JobSchedulerWorkerInstanceView>>();
        if (registry) boundary.Registry.Load = _ => workers.Task;
        else boundary.Load = _ => core.Task;
        var refresh = state.RefreshAsync();
        try
        {
            var acceptedCoreBeforeRetirement = state.Overview;
            var retirement = state.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.True((registry ? boundary.Registry.Token : boundary.CoreToken).IsCancellationRequested);
            boundary.Clock.UtcNow = InitialTime.AddHours(5);
            core.TrySetResult(Res.Ok(Snapshot(boundary.Clock.UtcNow)));
            workers.TrySetResult([Worker("late")]);
            await AssertCanceledAsync(refresh);
            await ObserveAsync(retirement);

            Assert.Same(registry ? acceptedCoreBeforeRetirement : accepted, state.Overview);
            Assert.Equal(coreAt, state.LastRefreshedUtc);
            Assert.Same(acceptedWorkers, state.Workers);
            Assert.Equal(registryAt, state.WorkerInsightObservedAtUtc);
            Assert.False(state.IsLoading);
            await AssertCanceledAsync(state.RefreshAsync());
        }
        finally
        {
            core.TrySetResult(Res.Ok(Snapshot()));
            workers.TrySetResult([]);
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task RefreshAsync_WhenProducerReturnsLiveCancellation_ShouldPreserveCancellationFeedback()
    {
        var boundary = new RuntimeBoundary
        {
            Load = _ => Task.FromCanceled<Res<JobSchedulerRuntimeOverview>>(new CancellationToken(canceled: true))
        };
        await using var state = boundary.Create();
        await AssertCanceledAsync(state.RefreshAsync());

        Assert.True(state.IsAuthorized);
        Assert.False(state.IsQueryFailed);
        Assert.Null(state.Error);
        Assert.False(state.HasLoadedSnapshot);
        Assert.False(state.IsLoading);
        boundary.Load = _ => Task.FromResult(Res.Ok(Snapshot()));
        await ObserveAsync(state.RefreshAsync());
        Assert.True(state.HasLoadedSnapshot);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InitializeAsync_WhenIntervalIsNonpositive_ShouldInitializeOnceWithoutTimer(int seconds)
    {
        var boundary = new RuntimeBoundary();
        await using var state = boundary.Create(interval: TimeSpan.FromSeconds(seconds));
        await ObserveAsync(state.InitializeAsync());
        await ObserveAsync(state.InitializeAsync());
        Assert.Equal(1, boundary.CoreCalls);
        Assert.Equal(1, boundary.Registry.Calls);
        Assert.Equal(0, boundary.Clock.CreateCalls);
    }

    [Fact]
    public async Task InitializeAsync_WhenTimerTicks_ShouldShareAutomaticReadAndDisposeOnlyOneLoop()
    {
        var boundary = new RuntimeBoundary();
        var entered = Signal();
        var pending = Signal<Res<JobSchedulerRuntimeOverview>>();
        var state = boundary.Create(interval: TimeSpan.FromSeconds(5));
        try
        {
            await ObserveAsync(state.InitializeAsync());
            await ObserveAsync(state.InitializeAsync());
            Assert.Equal(1, boundary.Clock.CreateCalls);
            var timer = Assert.Single(boundary.Clock.Timers);
            boundary.Load = _ => { entered.TrySetResult(); return pending.Task; };
            timer.Fire();
            await ObserveAsync(entered.Task);
            var joined = state.RefreshAsync();
            Assert.Equal(2, boundary.CoreCalls);
            pending.TrySetResult(Res.Ok(Snapshot()));
            await ObserveAsync(joined);
            var retirement = state.DisposeAsync().AsTask();
            await ObserveAsync(retirement);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            Assert.True(timer.IsDisposed);
            timer.Fire();
            Assert.Equal(2, boundary.CoreCalls);
        }
        finally
        {
            pending.TrySetResult(Res.Ok(Snapshot()));
            await ObserveAsync(state.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenTimerConstructionIsInProgress_ShouldDrainAndCloseLateTimerOutsideOwnerGate()
    {
        var boundary = new RuntimeBoundary();
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        boundary.Clock.OnCreate = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(ObservationTimeout, TestContext.Current.CancellationToken))
                throw new TimeoutException("Controlled timer creation was not released.");
        };
        var state = boundary.Create(interval: TimeSpan.FromSeconds(5));
        var initializing = Task.Run(() => state.InitializeAsync(), TestContext.Current.CancellationToken);
        Task retirement = Task.CompletedTask;
        Task retiring = Task.CompletedTask;
        try
        {
            await ObserveAsync(entered.Task);
            var reserved = Signal<Task>();
            retiring = Task.Run(() =>
            {
                var task = state.DisposeAsync().AsTask();
                reserved.TrySetResult(task);
                return task;
            }, TestContext.Current.CancellationToken);
            retirement = await ObserveAsync(reserved.Task);
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
            release.Set();
            await AssertCanceledAsync(initializing);
            await ObserveAsync(retiring);
            Assert.True(Assert.Single(boundary.Clock.Timers).IsDisposed);
        }
        finally
        {
            release.Set();
            try { await AssertCanceledAsync(initializing); }
            finally { await ObserveAsync(Task.WhenAll(retiring, state.DisposeAsync().AsTask())); }
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenCancellationAndTimerCleanupFault_ShouldObserveBothAndStillReleaseTimer()
    {
        var boundary = new RuntimeBoundary();
        boundary.Clock.OnDispose = () => throw new IOException("Timer cleanup failed");
        CancellationTokenRegistration registration = default;
        Task? reentrantRetirement = null;
        var state = boundary.Create(interval: TimeSpan.FromSeconds(5));
        boundary.Load = token =>
        {
            registration = token.Register(() =>
            {
                reentrantRetirement = state.DisposeAsync().AsTask();
                throw new IOException("Cancellation callback failed");
            });
            return Task.FromResult(Res.Ok(Snapshot()));
        };
        await ObserveAsync(state.InitializeAsync());
        var retirement = state.DisposeAsync().AsTask();
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => ObserveAsync(retirement));
            Assert.Contains(error.Flatten().InnerExceptions, failure => failure.Message == "Cancellation callback failed");
            Assert.Contains(error.Flatten().InnerExceptions, failure => failure.Message == "Timer cleanup failed");
            Assert.True(Assert.Single(boundary.Clock.Timers).IsDisposed);
            Assert.Same(retirement, reentrantRetirement);
            Assert.Same(retirement, state.DisposeAsync().AsTask());
        }
        finally
        {
            registration.Dispose();
            await Assert.ThrowsAsync<AggregateException>(() => ObserveAsync(state.DisposeAsync().AsTask()));
        }
    }

    private static Task ObserveAsync(Task task) => task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
    private static Task<T> ObserveAsync<T>(Task<T> task) => task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
    private static Task AssertCanceledAsync(Task task) => Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(task));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static JobSchedulerWorkerInstanceView Worker(string instanceId) => new()
    {
        AppId = "registry-service", AppName = "Registry service", ProjectName = "a-service-project",
        InstanceId = instanceId, IsLeader = false, RegisteredAtUtc = InitialTime.AddDays(-1),
        LastHeartbeatUtc = InitialTime.AddSeconds(-2), Status = JobSchedulerWorkerInstanceStatus.Online
    };

    private static JobSchedulerRuntimeOverview Snapshot(DateTimeOffset? capturedAt = null) => new()
    {
        Configuration = new()
        {
            SchedulerScopeKey = " runtime/上海?scope=one ", OwnerKey = "owner", WorkerInstanceId = "worker",
            AutonomousWork = true, CronTimeZoneId = "UTC", SchedulingPollInterval = TimeSpan.FromSeconds(1),
            WorkerPollInterval = TimeSpan.FromSeconds(1), SnapshotSyncInterval = TimeSpan.FromSeconds(5),
            CoordinatorLeaseDuration = TimeSpan.FromSeconds(30), MaxConcurrentStoreOperations = 8,
            ReservedLeaseStoreOperations = 2, StoreOperationTimeout = TimeSpan.FromSeconds(5),
            ExecutionLeaseDuration = TimeSpan.FromSeconds(30), ExecutionLeaseRenewInterval = TimeSpan.FromSeconds(5),
            ExecutionRetryDelay = TimeSpan.FromSeconds(1), ExecutionCancellationGracePeriod = TimeSpan.FromSeconds(2),
            WorkerShutdownGracePeriod = TimeSpan.FromSeconds(3), MaxWorkerExecutionThreads = 4, MaxClaimBatchSize = 4,
            MaxRecurringMaterializationsPerCycle = 8, MaxExpiredLeaseRecoveriesPerCycle = 8, EnableHistoryCleanup = true,
            HistoryCleanupInterval = TimeSpan.FromMinutes(1), MaxHistoryDeletionsPerCycle = 8,
            MaxExecutionHistoryEntriesPerExecution = 32, MaxExecutionHistoryMessageLength = 1024,
            MaxRetainedOrphanedExecutions = 8
        },
        Store = new() { Kind = "InMemory" },
        LocalPlane = new()
        {
            SchedulingReady = true, SchedulingMessage = null, WorkerReady = true,
            WorkerMessage = null, InFlightExecutions = 0
        },
        Owners = [], OwnerOnlineThreshold = TimeSpan.FromSeconds(15), CapturedAtUtc = capturedAt ?? InitialTime
    };

    private sealed class RuntimeBoundary
    {
        internal ControlledTimeProvider Clock { get; } = new();
        internal RegistryProvider Registry { get; } = new();
        internal Func<CancellationToken, Task<bool>> Authorize { get; set; } = _ => Task.FromResult(true);
        internal Func<CancellationToken, Task<Res<JobSchedulerRuntimeOverview>>> Load { get; set; } =
            _ => Task.FromResult(Res.Ok(Snapshot()));
        internal int AuthorizationCalls { get; private set; }
        internal int CoreCalls { get; private set; }
        internal CancellationToken AuthorizationToken { get; private set; }
        internal CancellationToken CoreToken { get; private set; }

        internal SchedulerRuntimePageState Create(bool withRegistry = true, TimeSpan interval = default) => new(token =>
        {
            CoreCalls++;
            CoreToken = token;
            return Load(token);
        }, token =>
        {
            AuthorizationCalls++;
            AuthorizationToken = token;
            return Authorize(token);
        }, Clock, interval, withRegistry ? Registry : null);
    }

    private sealed class RegistryProvider : IJobSchedulerWorkerInsightProvider
    {
        internal Func<CancellationToken, Task<IReadOnlyList<JobSchedulerWorkerInstanceView>>> Load { get; set; } =
            _ => Task.FromResult<IReadOnlyList<JobSchedulerWorkerInstanceView>>([]);
        internal int Calls { get; private set; }
        internal CancellationToken Token { get; private set; }
        public Task<IReadOnlyList<JobSchedulerWorkerInstanceView>> GetWorkerInstancesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            return Load(cancellationToken);
        }
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ControlledTimer> _timers = [];
        private DateTimeOffset _utcNow = InitialTime;
        private int _createCalls;
        internal Action? OnCreate { get; set; }
        internal Action? OnDispose { get; set; }
        internal int CreateCalls => Volatile.Read(ref _createCalls);
        internal ControlledTimer[] Timers { get { lock (_gate) return _timers.ToArray(); } }
        internal DateTimeOffset UtcNow
        {
            get { lock (_gate) return _utcNow; }
            set { lock (_gate) _utcNow = value; }
        }
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _createCalls);
            OnCreate?.Invoke();
            var timer = new ControlledTimer(callback, state, () => OnDispose?.Invoke());
            lock (_gate) _timers.Add(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, Action onDispose) : ITimer
    {
        private int _disposed;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        internal void Fire() { if (!IsDisposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose(); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
