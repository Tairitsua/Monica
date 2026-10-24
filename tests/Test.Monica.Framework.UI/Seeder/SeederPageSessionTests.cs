using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Framework.Seeder.Models;
using Monica.Framework.UI.UISeeder.State;
using Xunit;

namespace Test.Monica.Framework.UI.Seeder;

public sealed class SeederPageSessionTests
{
    [Fact]
    public async Task RunSelectedAsync_WhenAccessChanges_ShouldRejectBeforeExecutorCall()
    {
        var allowed = true;
        var executions = 0;
        var seeder = SeederUiTestData.CreateSeeder("HeldSeeder", SeederStatus.Held);
        var snapshot = SeederUiTestData.CreateSnapshot(SeederRunStatus.Held, [seeder]);
        await using var session = new SeederPageSession(
            _ => Task.FromResult(Res.Ok(snapshot)), _ => Task.FromResult(allowed), "test-host",
            runAsync: (_, _) =>
            {
                executions++;
                return Task.FromResult(Res.Ok(snapshot.Run));
            });
        await session.InitializeAsync();
        await session.OpenDetailsAsync(seeder);
        allowed = false;

        (await session.RunSelectedAsync()).Should().BeFalse();

        executions.Should().Be(0);
        session.IsAccessDenied.Should().BeTrue();
        session.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public async Task RunSelectedAsync_WhenBusy_ShouldRejectAnotherSubmissionAndRefreshOutcome()
    {
        var seeder = SeederUiTestData.CreateSeeder("HeldSeeder", SeederStatus.Held);
        var snapshot = SeederUiTestData.CreateSnapshot(SeederRunStatus.Held, [seeder]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        await using var session = new SeederPageSession(
            _ => Task.FromResult(Res.Ok(snapshot)), _ => Task.FromResult(true), "test-host",
            runAsync: async (identity, cancellationToken) =>
            {
                identity.Should().Be(seeder.SeederTypeName);
                executions++;
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                snapshot = SeederUiTestData.CreateSnapshot(SeederRunStatus.Succeeded,
                    [seeder with { Status = SeederStatus.Succeeded }]);
                return Res.Ok(snapshot.Run);
            });
        await session.InitializeAsync();
        await session.OpenDetailsAsync(seeder);
        var execution = session.RunSelectedAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        session.IsExecuting.Should().BeTrue();
        session.CanRunSelected.Should().BeFalse();
        (await session.RunSelectedAsync()).Should().BeFalse();
        release.TrySetResult();
        (await execution).Should().BeTrue();
        executions.Should().Be(1);
        session.IsExecuting.Should().BeFalse();
        session.SelectedSeeder!.Status.Should().Be(SeederStatus.Succeeded);
    }

    [Fact]
    public async Task DisposeAsync_WhenExecutionIsPending_ShouldCancelAndObserveIt()
    {
        var seeder = SeederUiTestData.CreateSeeder("HeldSeeder", SeederStatus.Held);
        var snapshot = SeederUiTestData.CreateSnapshot(SeederRunStatus.Held, [seeder]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new SeederPageSession(
            _ => Task.FromResult(Res.Ok(snapshot)), _ => Task.FromResult(true), "test-host",
            runAsync: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await never.Task.WaitAsync(cancellationToken);
                return Res.Ok(snapshot.Run);
            });
        await session.InitializeAsync();
        await session.OpenDetailsAsync(seeder);
        var execution = session.RunSelectedAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await session.DisposeAsync();

        (await execution).Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_ShouldExposeConfigurationAndComposableFilters()
    {
        var required = SeederUiTestData.CreateSeeder(
            dependencies: ["Example.Seeders.FoundationSeeder"]);
        var optional = SeederUiTestData.CreateSeeder(
            "SearchIndexSeeder",
            SeederStatus.Failed,
            SeederCriticality.Optional,
            SeederFailureBehavior.FailFast,
            errorMessage: "Search service unavailable");
        await using var session = CreateSession(_ => Task.FromResult(
            Res.Ok(SeederUiTestData.CreateSnapshot(
                SeederRunStatus.CompletedWithFailures,
                [required, optional]))));

        await session.InitializeAsync();
        await session.SetSearchTextAsync("search ");

        session.SearchText.Should().Be("search ");
        session.FilteredSeeders.Should().ContainSingle();

        await session.SetSearchTextAsync("search service");
        await session.SetStatusFilterAsync(SeederStatus.Failed);
        await session.SetCriticalityFilterAsync(SeederCriticality.Optional);
        await session.SetFailureBehaviorFilterAsync(SeederFailureBehavior.FailFast);

        session.LoadState.Should().Be(SeederPageLoadState.Ready);
        session.Snapshot!.Configuration.MaxConcurrency.Should().Be(4);
        session.ProgressPercent.Should().Be(100);
        session.FilteredSeeders.Should().ContainSingle()
            .Which.SeederName.Should().Be("SearchIndexSeeder");
    }

    [Fact]
    public async Task FilteredSeeders_ShouldPrioritizeAttentionStatesBeforeActiveAndSuccessfulWork()
    {
        var seeders = new[]
        {
            SeederUiTestData.CreateSeeder("Succeeded", SeederStatus.Succeeded),
            SeederUiTestData.CreateSeeder("Pending", SeederStatus.Pending),
            SeederUiTestData.CreateSeeder("Running", SeederStatus.Running),
            SeederUiTestData.CreateSeeder("Cancelled", SeederStatus.Cancelled),
            SeederUiTestData.CreateSeeder("Blocked", SeederStatus.Blocked),
            SeederUiTestData.CreateSeeder("Failed", SeederStatus.Failed)
        };
        await using var session = CreateSession(_ => Task.FromResult(Res.Ok(
            SeederUiTestData.CreateSnapshot(SeederRunStatus.Running, seeders))));

        await session.InitializeAsync();

        session.FilteredSeeders.Select(static seeder => seeder.Status).Should().Equal(
            SeederStatus.Failed,
            SeederStatus.Blocked,
            SeederStatus.Cancelled,
            SeederStatus.Running,
            SeederStatus.Pending,
            SeederStatus.Succeeded);
    }

    [Fact]
    public async Task RefreshAsync_WhenRequestIsActive_ShouldJoinTrackedRequestWithoutOverlap()
    {
        var facadeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFacade = new TaskCompletionSource<Res<SeederDiagnosticsSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeCalls = 0;
        await using var session = CreateSession(async _ =>
        {
            Interlocked.Increment(ref facadeCalls);
            facadeEntered.SetResult();
            return await releaseFacade.Task;
        });

        var first = session.RefreshAsync();
        await facadeEntered.Task;
        var second = session.RefreshAsync();

        second.Should().BeSameAs(first);
        facadeCalls.Should().Be(1);

        releaseFacade.SetResult(Res.Ok(SeederUiTestData.CreateSnapshot()));
        (await first).Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAsync_WhenLatestRequestFails_ShouldPreservePreviousSnapshot()
    {
        var initial = SeederUiTestData.CreateSnapshot();
        var facadeCalls = 0;
        await using var session = CreateSession(_ =>
        {
            facadeCalls++;
            return facadeCalls == 1
                ? Task.FromResult(Res.Ok(initial))
                : Task.FromResult<Res<SeederDiagnosticsSnapshot>>("refresh unavailable");
        });

        await session.InitializeAsync();
        (await session.RefreshAsync()).Should().BeFalse();

        session.Snapshot.Should().BeSameAs(initial);
        session.LoadState.Should().Be(SeederPageLoadState.Ready);
        session.RefreshError.Should().Be("refresh unavailable");
    }

    [Fact]
    public async Task Polling_WhenFailuresRepeat_ShouldKeepRefreshErrorDuringNextRequest()
    {
        var timeProvider = new TriggerTimeProvider();
        var firstFailureCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repeatedFailureEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repeatedFailureCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRepeatedFailure = new TaskCompletionSource<Res<SeederDiagnosticsSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeCalls = 0;
        await using var session = CreateSession(
            _ =>
            {
                var call = Interlocked.Increment(ref facadeCalls);
                return call switch
                {
                    1 => Task.FromResult(Res.Ok(SeederUiTestData.CreateSnapshot(SeederRunStatus.Running))),
                    2 => Task.FromResult<Res<SeederDiagnosticsSnapshot>>("refresh unavailable"),
                    _ => WaitForRepeatedFailureAsync()
                };

                async Task<Res<SeederDiagnosticsSnapshot>> WaitForRepeatedFailureAsync()
                {
                    repeatedFailureEntered.TrySetResult();
                    return await releaseRepeatedFailure.Task;
                }
            },
            timeProvider: timeProvider);
        session.Changed += () =>
        {
            if (session.RefreshError == "refresh unavailable" && !session.IsRefreshing)
            {
                if (facadeCalls == 2)
                {
                    firstFailureCompleted.TrySetResult();
                }
                else if (facadeCalls == 3)
                {
                    repeatedFailureCompleted.TrySetResult();
                }
            }

            return Task.CompletedTask;
        };

        await session.InitializeAsync();
        timeProvider.Tick().Should().Be(1);
        await firstFailureCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        timeProvider.Tick().Should().Be(1);
        await repeatedFailureEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        session.IsRefreshing.Should().BeTrue();
        session.RefreshError.Should().Be("refresh unavailable");

        releaseRepeatedFailure.SetResult("refresh unavailable");
        await repeatedFailureCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        session.RefreshError.Should().Be("refresh unavailable");
    }

    [Fact]
    public async Task InitializeAsync_WhenAccessIsDenied_ShouldNeverInvokeFacade()
    {
        var facadeCalls = 0;
        await using var session = CreateSession(
            _ =>
            {
                facadeCalls++;
                return Task.FromResult(Res.Ok(SeederUiTestData.CreateSnapshot()));
            },
            _ => Task.FromResult(false));

        await session.InitializeAsync();

        session.IsAccessDenied.Should().BeTrue();
        session.Snapshot.Should().BeNull();
        facadeCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenAuthorizationIsRevoked_ShouldPreserveSnapshotAndStopPolling()
    {
        var timeProvider = new TriggerTimeProvider();
        var snapshot = SeederUiTestData.CreateSnapshot(SeederRunStatus.Running);
        var authorizationCalls = 0;
        var facadeCalls = 0;
        await using var session = CreateSession(
            _ =>
            {
                facadeCalls++;
                return Task.FromResult(Res.Ok(snapshot));
            },
            _ => Task.FromResult(Interlocked.Increment(ref authorizationCalls) == 1),
            timeProvider: timeProvider);

        await session.InitializeAsync();
        (await session.RefreshAsync()).Should().BeFalse();

        authorizationCalls.Should().Be(2);
        facadeCalls.Should().Be(1);
        session.Snapshot.Should().BeSameAs(snapshot);
        session.LoadState.Should().Be(SeederPageLoadState.Ready);
        session.IsAccessDenied.Should().BeTrue();
        timeProvider.ActiveTimerCount.Should().Be(0);
        timeProvider.Tick().Should().Be(0);
    }

    [Fact]
    public async Task Polling_ShouldStopAfterRunBecomesTerminal()
    {
        var timeProvider = new TriggerTimeProvider();
        var terminalRefreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeCalls = 0;
        await using var session = CreateSession(
            _ =>
            {
                var call = Interlocked.Increment(ref facadeCalls);
                if (call == 1)
                {
                    return Task.FromResult(Res.Ok(SeederUiTestData.CreateSnapshot(SeederRunStatus.Running)));
                }

                return Task.FromResult(Res.Ok(SeederUiTestData.CreateSnapshot(SeederRunStatus.Succeeded)));
            },
            timeProvider: timeProvider);
        session.Changed += () =>
        {
            if (session.Snapshot?.Run.Status == SeederRunStatus.Succeeded && !session.IsRefreshing)
            {
                terminalRefreshCompleted.TrySetResult();
            }

            return Task.CompletedTask;
        };

        await session.InitializeAsync();
        timeProvider.ActiveTimerCount.Should().Be(1);
        timeProvider.Tick().Should().Be(1);
        await terminalRefreshCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        facadeCalls.Should().Be(2);
        session.Snapshot!.Run.Status.Should().Be(SeederRunStatus.Succeeded);
        timeProvider.ActiveTimerCount.Should().Be(0);
        timeProvider.Tick().Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_WhenAuthorizationIsBlocked_ShouldIgnoreLateDenialAndDrainRefresh()
    {
        var authorizationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuthorization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeCalls = 0;
        var changedCalls = 0;
        var session = CreateSession(
            _ =>
            {
                facadeCalls++;
                return Task.FromResult(Res.Ok(SeederUiTestData.CreateSnapshot()));
            },
            _ =>
            {
                authorizationEntered.TrySetResult();
                return releaseAuthorization.Task;
            });
        session.Changed += () =>
        {
            changedCalls++;
            return Task.CompletedTask;
        };

        var refresh = session.RefreshAsync();
        await authorizationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        var dispose = session.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse();

        releaseAuthorization.SetResult(false);
        await dispose;

        (await refresh).Should().BeFalse();
        session.IsAccessDenied.Should().BeFalse();
        session.Snapshot.Should().BeNull();
        facadeCalls.Should().Be(0);
        changedCalls.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_WhenCancellationCallbackThrows_ShouldStillReleasePollingResources()
    {
        var timeProvider = new TriggerTimeProvider();
        var registration = default(CancellationTokenRegistration);
        var session = CreateSession(
            cancellationToken =>
            {
                registration = cancellationToken.Register(static () =>
                    throw new InvalidOperationException("Expected cancellation callback failure."));
                return Task.FromResult(Res.Ok(SeederUiTestData.CreateSnapshot(SeederRunStatus.Running)));
            },
            timeProvider: timeProvider);

        await session.InitializeAsync();
        timeProvider.ActiveTimerCount.Should().Be(1);

        Func<Task> dispose = async () => await session.DisposeAsync();
        var exception = await dispose.Should().ThrowAsync<AggregateException>();

        exception.Which.InnerExceptions.Should().ContainSingle()
            .Which.Should().BeOfType<InvalidOperationException>();
        timeProvider.ActiveTimerCount.Should().Be(0);
        registration.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_ShouldCancelAndAwaitTrackedRefresh()
    {
        var facadeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(async cancellationToken =>
        {
            facadeEntered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Res.Ok(SeederUiTestData.CreateSnapshot());
            }
            finally
            {
                facadeExited.SetResult();
            }
        });
        var refresh = session.RefreshAsync();
        await facadeEntered.Task;

        await session.DisposeAsync();

        await facadeExited.Task;
        (await refresh).Should().BeFalse();
        Func<Task> disposeAgain = () => session.DisposeAsync().AsTask();
        await disposeAgain.Should().NotThrowAsync();
    }

    private static SeederPageSession CreateSession(
        Func<CancellationToken, Task<Res<SeederDiagnosticsSnapshot>>> getSnapshot,
        Func<CancellationToken, Task<bool>>? authorize = null,
        TimeProvider? timeProvider = null)
    {
        return new SeederPageSession(
            getSnapshot,
            authorize ?? (_ => Task.FromResult(true)),
            "test-host",
            timeProvider,
            refreshInterval: TimeSpan.FromHours(1));
    }

    private sealed class TriggerTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<TriggerTimer> _timers = [];

        public int ActiveTimerCount
        {
            get
            {
                lock (_sync)
                {
                    return _timers.Count(static timer => timer.IsActive);
                }
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new TriggerTimer(callback, state, dueTime, period);
            lock (_sync)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public int Tick()
        {
            TriggerTimer[] timers;
            lock (_sync)
            {
                timers = [.. _timers];
            }

            return timers.Count(static timer => timer.TryFire());
        }

        private sealed class TriggerTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly object _sync = new();
            private TimeSpan _period = period;
            private bool _isActive = dueTime != Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool IsActive
            {
                get
                {
                    lock (_sync)
                    {
                        return _isActive && !_disposed;
                    }
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _period = period;
                    _isActive = dueTime != Timeout.InfiniteTimeSpan;
                    return true;
                }
            }

            public bool TryFire()
            {
                lock (_sync)
                {
                    if (_disposed || !_isActive)
                    {
                        return false;
                    }

                    if (_period == Timeout.InfiniteTimeSpan)
                    {
                        _isActive = false;
                    }
                }

                callback(state);
                return true;
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    _disposed = true;
                    _isActive = false;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
