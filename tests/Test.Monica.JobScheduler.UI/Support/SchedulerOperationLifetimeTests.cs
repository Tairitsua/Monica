using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Xunit;

namespace Test.Monica.JobScheduler.UI.Support;

public sealed class SchedulerOperationLifetimeTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_WhenProducerRequestsRetirement_ShouldReserveItsActualCompletionFirst()
    {
        var owner = new SchedulerOperationLifetime();
        var result = Signal<int>();
        Task? retirement = null;
        var cleanupCalls = 0;
        var producerCalls = 0;
        var operation = owner.RunAsync(_ =>
        {
            producerCalls++;
            retirement = owner.RetireAsync(() => cleanupCalls++).AsTask();
            return result.Task;
        });
        try
        {
            Assert.NotNull(retirement);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, cleanupCalls);
            Assert.False(owner.TryApply(() => throw new InvalidOperationException("Retired evidence adopted.")));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.RunAsync(_ =>
            {
                producerCalls++;
                return Task.FromResult(100);
            }));
            result.TrySetResult(42);
            Assert.Equal(42, await operation.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken));
            await ObserveAsync(retirement);
            Assert.Equal(1, producerCalls);
            Assert.Equal(1, cleanupCalls);
            Assert.Same(retirement, owner.RetireAsync(() => cleanupCalls++).AsTask());
        }
        finally
        {
            result.TrySetResult(42);
            await ObserveAsync(owner.RetireAsync(() => cleanupCalls++).AsTask());
        }
    }

    [Fact]
    public async Task RetireAsync_WhenCancellationCallbackThrows_ShouldStillDrainProducerAndCleanup()
    {
        var owner = new SchedulerOperationLifetime();
        var result = Signal<int>();
        var cancellationFailure = new IOException("Cancellation callback failed.");
        var cleanupCalls = 0;
        Task? nestedRetirement = null;
        CancellationTokenRegistration registration = default;
        var operation = owner.RunAsync(token =>
        {
            registration = token.Register(() =>
            {
                nestedRetirement = owner.RetireAsync(() => cleanupCalls++).AsTask();
                throw cancellationFailure;
            });
            return result.Task;
        });
        var retirement = owner.RetireAsync(() => cleanupCalls++).AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, cleanupCalls);
            result.TrySetResult(5);
            await ObserveAsync(operation);
            var failure = await Assert.ThrowsAsync<AggregateException>(() => ObserveAsync(retirement));
            Assert.Contains(cancellationFailure, failure.Flatten().InnerExceptions);
            Assert.Same(retirement, nestedRetirement);
            Assert.Equal(1, cleanupCalls);
        }
        finally
        {
            result.TrySetResult(5);
            await ObserveAsync(operation);
            await Assert.ThrowsAsync<AggregateException>(() => ObserveAsync(owner.RetireAsync(() => cleanupCalls++).AsTask()));
            registration.Dispose();
        }
    }

    [Fact]
    public async Task RetireAsync_WhenWorkWaitsOnSerialization_ShouldDrainBothReservationsBeforeReleasingGate()
    {
        var owner = new SchedulerOperationLifetime();
        using var gate = new SemaphoreSlim(1, 1);
        var release = Signal<bool>();
        var entered = Signal<bool>();
        var producers = 0;
        async Task<int> ProduceAsync(CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                producers++;
                entered.TrySetResult(true);
                await release.Task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
                token.ThrowIfCancellationRequested();
                return 1;
            }
            finally { gate.Release(); }
        }
        var first = owner.RunAsync(ProduceAsync);
        await ObserveAsync(entered.Task);
        var second = owner.RunAsync(ProduceAsync);
        var cleanupCalled = false;
        var retirement = owner.RetireAsync(() => cleanupCalled = true).AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.False(cleanupCalled);
            release.TrySetResult(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(first));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObserveAsync(second));
            await ObserveAsync(retirement);
            Assert.True(cleanupCalled);
            Assert.Equal(1, producers);
        }
        finally
        {
            release.TrySetResult(true);
            await ObserveAsync(owner.RetireAsync(() => cleanupCalled = true).AsTask());
        }
    }

    [Fact]
    public async Task RetireAsync_WhenCleanupIsAsynchronous_ShouldJoinItsActualCompletion()
    {
        var owner = new SchedulerOperationLifetime();
        var release = Signal<bool>();
        var cleanupStarted = Signal<bool>();
        var retirement = owner.RetireAsync(async () =>
        {
            cleanupStarted.TrySetResult(true);
            await ObserveAsync(release.Task);
        }).AsTask();
        try
        {
            await ObserveAsync(cleanupStarted.Task);
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, owner.RetireAsync(() => { }).AsTask());
            release.TrySetResult(true);
            await ObserveAsync(retirement);
        }
        finally
        {
            release.TrySetResult(true);
            await ObserveAsync(owner.RetireAsync(() => { }).AsTask());
        }
    }

    [Fact]
    public async Task RunAsync_WhenExternalProducerAndCleanupReadOwnerFromAnotherThread_ShouldNotHoldGate()
    {
        var owner = new SchedulerOperationLifetime();
        var readers = new List<Task>();
        var observed = new List<bool>();
        void ReadOwner()
        {
            using var signal = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                observed.Add(owner.IsRetired);
                signal.Set();
            }, TestContext.Current.CancellationToken);
            readers.Add(reader);
            if (!signal.Wait(ObservationTimeout, TestContext.Current.CancellationToken))
                throw new TimeoutException("An external callback ran while admission was locked.");
        }
        try
        {
            await ObserveAsync(owner.RunAsync(_ =>
            {
                ReadOwner();
                return Task.CompletedTask;
            }));
            await ObserveAsync(owner.RetireAsync(ReadOwner).AsTask());
            await ObserveAsync(Task.WhenAll(readers));
            Assert.Equal(new[] { false, true }, observed);
        }
        finally
        {
            await ObserveAsync(owner.RetireAsync(() => { }).AsTask());
            await ObserveAsync(Task.WhenAll(readers));
        }
    }

    private static Task ObserveAsync(Task task) => task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
