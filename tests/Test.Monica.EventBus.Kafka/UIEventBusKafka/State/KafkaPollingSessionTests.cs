using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.EventBus.Kafka.Facades;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.UIEventBusKafka.State;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Kafka.UIEventBusKafka.State;

public sealed class KafkaPollingSessionTests
{
    [Fact]
    public async Task StateChanged_WhenIntervalChanges_ShouldAllowAnotherThreadToReadPublicSamplerState()
    {
        // This configuration-only control never calls the facade or selects a broker.
        var facade = new KafkaConsoleFacade(null!, null!, null!, null!, null!, null!);
        using var notificationEntered = new ManualResetEventSlim();
        using var stateRead = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        await using var state = new KafkaPerformancePollingState(facade, Options.Create(new ModuleEventBusKafkaUIOption
        {
            EnablePerformanceAutoRefresh = false,
            PerformanceRefreshInterval = TimeSpan.FromSeconds(60)
        }));
        var observedInterval = 0;
        var reader = Task.Run(() =>
        {
            notificationEntered.Wait(token);
            observedInterval = state.RefreshIntervalSeconds;
            stateRead.Set();
        }, token);
        state.StateChanged += () =>
        {
            notificationEntered.Set();
            if (!stateRead.Wait(TimeSpan.FromSeconds(10), token))
                throw new TimeoutException("The subscriber could not coordinate with the sampler reader.");
        };

        try
        {
            await state.SetRefreshIntervalAsync(5, token);
            await reader.WaitAsync(TimeSpan.FromSeconds(10), token);
            observedInterval.Should().Be(5);
            state.RefreshIntervalSeconds.Should().Be(5);
        }
        finally
        {
            notificationEntered.Set();
            await reader.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
    }

    [Fact]
    public async Task Capture_WhenInitialMetadataFails_ShouldKeepDefaultCountsUnavailable()
    {
        var failed = new KafkaPerformanceSnapshot { ClusterId = "primary", Message = "Broker metadata failed." };
        await using var session = Create((_, _) => Task.FromResult(Result.Success(failed)));
        await session.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);

        (await session.CaptureAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        session.Snapshot.Should().BeNull();
        session.LastCapturedAt.Should().BeNull();
        session.LastAttemptedAt.Should().Be(failed.CapturedAt);
        session.LastError.Should().Be(failed.Message);
    }

    [Fact]
    public async Task Capture_WhenLaterMetadataFails_ShouldRetainLastValidEvidenceAndAttemptDiagnostic()
    {
        var valid = Snapshot("primary");
        var failed = new KafkaPerformanceSnapshot
        {
            ClusterId = "primary", CapturedAt = valid.CapturedAt.AddSeconds(10),
            Message = "Topic metadata failed after querying the broker.", BrokerCount = 1
        };
        var next = valid;
        await using var session = Create((_, _) => Task.FromResult(Result.Success(next)));
        await session.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);
        (await session.CaptureAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        next = failed;

        (await session.CaptureAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        session.Snapshot.Should().BeSameAs(valid);
        session.LastCapturedAt.Should().Be(valid.CapturedAt);
        session.LastAttemptedAt.Should().Be(failed.CapturedAt);
        session.LastError.Should().Be(failed.Message);
    }

    [Fact]
    public async Task Capture_WhenOnlyOffsetsAreIncomplete_ShouldPreserveVerifiedMetadataAndUnknownMetrics()
    {
        var partial = Snapshot("primary");
        partial.Message = "One committed-offset batch was incomplete.";
        partial.TotalLag = null;
        partial.MessageConsumeRatePerSecond = null;
        await using var session = Create((_, _) => Task.FromResult(Result.Success(partial)));
        await session.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);

        (await session.CaptureAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        session.Snapshot.Should().BeSameAs(partial);
        session.Snapshot!.HasMetadataSnapshot.Should().BeTrue();
        session.Snapshot.TotalLag.Should().BeNull();
        session.Snapshot.MessageConsumeRatePerSecond.Should().BeNull();
        session.LastError.Should().Be(partial.Message);
    }

    [Fact]
    public async Task Activate_WhenTargetChangesDuringCancellationIgnoringCapture_ShouldDrainWithoutPublishingOldEvidence()
    {
        var admitted = NewSignal();
        var release = NewSignal();
        await using var session = Create(async (target, _) =>
        {
            if (target.ClusterId == "old")
            {
                admitted.TrySetResult();
                await release.Task;
            }
            return Result.Success(Snapshot(target.ClusterId));
        });
        await session.ActivateAsync(new KafkaSamplingTarget("old"), TestContext.Current.CancellationToken);
        var oldCapture = session.CaptureAsync(TestContext.Current.CancellationToken);
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var transition = session.ActivateAsync(new KafkaSamplingTarget("new"), TestContext.Current.CancellationToken);
        transition.IsCompleted.Should().BeFalse("the actual old capture still owns its resources");
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldCapture);
        await transition;
        session.Snapshot.Should().BeNull();

        (await session.CaptureAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
        session.Snapshot!.ClusterId.Should().Be("new");
    }

    [Fact]
    public async Task Dispose_WhenCaptureIgnoresCancellation_ShouldDrainItAndRejectNewAdmission()
    {
        var admitted = NewSignal();
        var release = NewSignal();
        var notifications = 0;
        var session = Create(async (target, _) =>
        {
            admitted.TrySetResult();
            await release.Task;
            return Result.Success(Snapshot(target.ClusterId));
        });
        session.StateChanged += () => notifications++;
        await session.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);
        var capture = session.CaptureAsync(TestContext.Current.CancellationToken);
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var retirement = session.DisposeAsync().AsTask();
        var retiredNotifications = notifications;
        retirement.IsCompleted.Should().BeFalse();
        session.DisposeAsync().AsTask().Should().BeSameAs(retirement);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CaptureAsync(TestContext.Current.CancellationToken));
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        await retirement.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        notifications.Should().Be(retiredNotifications);
        session.Snapshot.Should().BeNull();
    }

    [Fact]
    public async Task Dispose_WhenControlsAndCapturesAreQueued_ShouldDrainEveryAdmittedOperation()
    {
        var admitted = NewSignal();
        var release = NewSignal();
        var session = Create(async (target, _) =>
        {
            admitted.TrySetResult();
            await release.Task;
            return Result.Success(Snapshot(target.ClusterId));
        }, enabled: true);
        var activation = session.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queuedControl = session.SetEnabledAsync(false, TestContext.Current.CancellationToken);
        var queuedCapture = session.CaptureAsync(TestContext.Current.CancellationToken);

        var retirement = session.DisposeAsync().AsTask();
        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedControl);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedCapture);
        await retirement.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Activate_WhenMatchingRepositoryEvidenceWasLoaded_ShouldRetainItWithAutomaticSamplingDisabled()
    {
        var valid = Snapshot("primary");
        await using var session = Create((target, _) => Task.FromResult(Result.Success(Snapshot(target.ClusterId))));
        session.SetSnapshot(valid, new KafkaSamplingTarget("primary"));

        await session.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);

        session.Snapshot.Should().BeSameAs(valid);
        session.LastCapturedAt.Should().Be(valid.CapturedAt);
    }

    [Fact]
    public async Task Deactivate_WhenAnotherSessionExists_ShouldOnlyStopTheOwningSession()
    {
        var firstCount = 0;
        var secondCount = 0;
        await using var first = Create((target, _) =>
        {
            firstCount++;
            return Task.FromResult(Result.Success(Snapshot(target.ClusterId)));
        });
        await using var second = Create((target, _) =>
        {
            secondCount++;
            return Task.FromResult(Result.Success(Snapshot(target.ClusterId)));
        });
        await first.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);
        await second.ActivateAsync(new KafkaSamplingTarget("primary"), TestContext.Current.CancellationToken);
        await first.CaptureAsync(TestContext.Current.CancellationToken);
        await first.DeactivateAsync();
        await first.DisposeAsync();

        (await second.CaptureAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        firstCount.Should().Be(1);
        secondCount.Should().Be(1);
        second.Snapshot!.ClusterId.Should().Be("primary");
    }

    [Fact]
    public async Task Capture_WhenTargetContainsOpaqueIdentity_ShouldKeepEveryIdentityCharacter()
    {
        var expected = new KafkaSamplingTarget("review:flight/上海?revision=2026#inspection%25", " group/worker?region=APAC#v2 ", "orders.v2");
        KafkaSamplingTarget? actual = null;
        await using var session = Create((target, _) =>
        {
            actual = target;
            return Task.FromResult(Result.Success(Snapshot(target.ClusterId)));
        });
        await session.ActivateAsync(expected, TestContext.Current.CancellationToken);

        await session.CaptureAsync(TestContext.Current.CancellationToken);

        actual.Should().Be(expected);
    }

    private static KafkaPollingSession<KafkaPerformanceSnapshot> Create(
        Func<KafkaSamplingTarget, CancellationToken, Task<Result<KafkaPerformanceSnapshot>>> capture,
        bool enabled = false) => new(capture, snapshot => snapshot.HasMetadataSnapshot,
        snapshot => snapshot.Message, snapshot => snapshot.CapturedAt, enabled, TimeSpan.FromSeconds(60));

    private static KafkaPerformanceSnapshot Snapshot(string clusterId) => new()
    {
        ClusterId = clusterId, HasMetadataSnapshot = true, BrokerCount = 1,
        CapturedAt = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero)
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
