using Microsoft.Extensions.Logging.Abstractions;
using Monica.Locker.Leases;
using Monica.Testing.Time;
using NSubstitute;
using Xunit;

namespace Test.Monica.Locker.Leases;

public sealed class LeaseCoordinatorTests
{
    private static readonly DateTimeOffset START = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly LeaseResource RESOURCE = new("test", "maintenance");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_WhenNoWorkRuns_ShouldCacheTheTermAndRenewIndependently()
    {
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        await using var coordinator = Create(store, time);
        var first = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        Assert.Same(first, await coordinator.GetAsync(RESOURCE, Token));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(20), Token);

        Assert.False(first.StoppingToken.IsCancellationRequested);
        Assert.Same(first, await coordinator.GetAsync(RESOURCE, Token));
        var snapshot = await store.GetSnapshotAsync(RESOURCE.Scope, RESOURCE.Name, Token);
        Assert.Equal(START.AddSeconds(40), Assert.Single(snapshot.Leases).ExpiresAtUtc);
    }

    [Fact]
    public async Task GetAsync_WhenProcessPausesPastExpiry_ShouldCancelOldTermAndAcquireAFreshIdentity()
    {
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        await using var coordinator = Create(store, time);
        var first = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.AdvanceAfterPause(TimeSpan.FromSeconds(31));

        Assert.True(first.StoppingToken.IsCancellationRequested);
        var second = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        Assert.NotEqual(first.Identity.TermId, second.Identity.TermId);
        Assert.False(second.StoppingToken.IsCancellationRequested);
        Assert.False(await store.TryReleaseAsync(first.Identity, Token));
    }

    [Fact]
    public async Task GetAsync_WhenWallClockMovesBackward_ShouldStillExpireUsingElapsedTime()
    {
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        await using var coordinator = Create(store, time);
        var first = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        time.AdjustWallClock(TimeSpan.FromHours(-1));
        time.AdvanceAfterPause(TimeSpan.FromSeconds(30));

        Assert.True(first.StoppingToken.IsCancellationRequested);
        Assert.Null(await coordinator.GetAsync(RESOURCE, Token));
    }

    [Fact]
    public async Task Renewal_WhenProviderStalls_ShouldRetireWithoutAccumulatingCallsOrAcceptingALateGrant()
    {
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<LeaseGrant?>(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.TryRenewAsync(Arg.Any<LeaseIdentity>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { entered.TrySetResult(); return reply.Task; });
        await using var coordinator = Create(boundary, time);
        var first = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = first.StoppingToken.Register(() => lost.TrySetResult());
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(Token);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(15), Token);
        time.Advance(TimeSpan.FromSeconds(5));
        await lost.Task.WaitAsync(Token);
        Assert.Null(await coordinator.GetAsync(RESOURCE, Token));
        Assert.Null(await coordinator.GetAsync(RESOURCE, Token));
        await boundary.Received(1).TryAcquireAsync(RESOURCE, "holder", TimeSpan.FromSeconds(30), Arg.Any<CancellationToken>());
        await boundary.Received(1).TryRenewAsync(first.Identity, TimeSpan.FromSeconds(30), Arg.Any<CancellationToken>());

        reply.SetResult(await durable.TryRenewAsync(first.Identity, TimeSpan.FromSeconds(30), Token));
        Assert.True(first.StoppingToken.IsCancellationRequested);
        Assert.Null(await coordinator.GetAsync(RESOURCE, Token));
    }

    [Fact]
    public async Task GetAsync_WhenProviderConfirmsNoRemainingValidity_ShouldNotAdmitWork()
    {
        var time = new ControllableTimeProvider(START);
        var boundary = Substitute.For<IExclusiveLeaseStore>();
        boundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // A provider can confirm a shorter actual lifetime than requested.
                return Task.FromResult<LeaseGrant?>(new(new(RESOURCE, "holder", "short-grant"), TimeSpan.Zero, time.GetUtcNow()));
            });
        await using var coordinator = Create(boundary, time);
        Assert.Null(await coordinator.GetAsync(RESOURCE, Token));
    }

    [Fact]
    public async Task GetAsync_WhenWallTimePassesDuringAcquisitionWithoutTimerProgress_ShouldNotAdmitWork()
    {
        var time = new ControllableTimeProvider(START);
        var boundary = Substitute.For<IExclusiveLeaseStore>();
        boundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                time.AdjustWallClock(TimeSpan.FromMinutes(1));
                return Task.FromResult<LeaseGrant?>(new(new(RESOURCE, "holder", "late-grant"), TimeSpan.FromSeconds(30), START));
            });
        await using var coordinator = Create(boundary, time);
        Assert.Null(await coordinator.GetAsync(RESOURCE, Token));
    }

    [Fact]
    public async Task DisposeAsync_WhenTheOwnerHasDrained_ShouldReleaseOnlyItsOwnTerm()
    {
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        var coordinator = Create(store, time);
        var first = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        await coordinator.DisposeAsync();
        Assert.True(first.StoppingToken.IsCancellationRequested);
        var successor = Assert.IsType<LeaseGrant>(await store.TryAcquireAsync(RESOURCE, "successor", TimeSpan.FromSeconds(30), Token));
        await coordinator.DisposeAsync();
        Assert.NotNull(await store.TryRenewAsync(successor.Identity, TimeSpan.FromSeconds(30), Token));
    }

    [Fact]
    public async Task DisposeAsync_WhenSeveralReleasesArePending_ShouldStartThemTogetherAndShareCleanupAcrossCallers()
    {
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        boundary.TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (Interlocked.Increment(ref entered) == 3) allEntered.TrySetResult();
                await finish.Task;
                return await durable.TryReleaseAsync(call.Arg<LeaseIdentity>(), call.Arg<CancellationToken>());
            });
        using var coordinator = Create(boundary, time);
        for (var index = 0; index < 3; index++)
            Assert.NotNull(await coordinator.GetAsync(new LeaseResource("test", $"role-{index}"), Token));

        var first = coordinator.DisposeAsync().AsTask();
        var second = coordinator.DisposeAsync().AsTask();
        try
        {
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(first, second);
        Assert.Empty((await durable.GetSnapshotAsync("test", "role-", Token)).Leases);
        await boundary.Received(3).TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_WhenOneReleaseFails_ShouldStillReleaseEveryOtherResourceAndRetainItsFailure()
    {
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        var failure = new IOException("The release response was lost.");
        boundary.TryReleaseAsync(Arg.Is<LeaseIdentity>(identity => identity.Resource == RESOURCE), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(failure));
        using var coordinator = Create(boundary, time);
        Assert.NotNull(await coordinator.GetAsync(RESOURCE, Token));
        Assert.NotNull(await coordinator.GetAsync(new LeaseResource("test", "independent"), Token));

        var cleanup = coordinator.DisposeAsync().AsTask();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => cleanup));
        Assert.Same(cleanup, coordinator.DisposeAsync().AsTask());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => coordinator.DisposeAsync().AsTask()));
        Assert.Equal(RESOURCE, Assert.Single((await durable.GetSnapshotAsync("test", "", Token)).Leases).Resource);
        await boundary.Received(2).TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_WhenARequestNeverDrains_ShouldReleaseIndependentRolesAndExpireItsRealTimeBudget()
    {
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        var renewalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalReply = new TaskCompletionSource<LeaseGrant?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var independentReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.TryRenewAsync(Arg.Any<LeaseIdentity>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.Arg<LeaseIdentity>().Resource != RESOURCE)
                    return durable.TryRenewAsync(call.Arg<LeaseIdentity>(), call.Arg<TimeSpan>(), call.Arg<CancellationToken>());
                renewalEntered.TrySetResult();
                return renewalReply.Task;
            });
        boundary.TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var result = await durable.TryReleaseAsync(call.Arg<LeaseIdentity>(), call.Arg<CancellationToken>());
                independentReleased.TrySetResult();
                return result;
            });
        using var coordinator = new LeaseCoordinator(boundary, "holder", time, NullLogger.Instance,
            new LeaseCoordinatorOptions { OperationTimeout = TimeSpan.FromSeconds(1) });
        Assert.NotNull(await coordinator.GetAsync(RESOURCE, Token));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await renewalEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.NotNull(await coordinator.GetAsync(new LeaseResource("test", "independent"), Token));

        var cleanup = coordinator.DisposeAsync().AsTask();
        try
        {
            await independentReleased.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await Assert.ThrowsAsync<TimeoutException>(() => cleanup.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.True(cleanup.IsCompleted);
            Assert.Equal(START.AddSeconds(10), time.GetUtcNow());
            Assert.Equal(RESOURCE, Assert.Single((await durable.GetSnapshotAsync("test", "", Token)).Leases).Resource);
            await boundary.Received(1).TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>());
        }
        finally { renewalReply.TrySetResult(null); }
    }

    [Fact]
    public async Task Dispose_WhenAcquisitionIsInFlight_ShouldRejectTheLateGrant()
    {
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<LeaseGrant?>(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { entered.TrySetResult(); return reply.Task; });
        var coordinator = Create(boundary, time);
        var acquiring = coordinator.GetAsync(RESOURCE, Token);
        await entered.Task.WaitAsync(Token);
        coordinator.Dispose();
        var late = await durable.TryAcquireAsync(RESOURCE, "holder", TimeSpan.FromSeconds(30), Token);
        reply.SetResult(late);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        await coordinator.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.GetAsync(RESOURCE, Token));
        Assert.Null(await durable.TryAcquireAsync(RESOURCE, "successor", TimeSpan.FromSeconds(30), Token));
    }

    private static LeaseCoordinator Create(IExclusiveLeaseStore store, TimeProvider time) =>
        new(store, "holder", time, NullLogger.Instance);

    private static IExclusiveLeaseStore ForwardTo(InMemoryLeaseStore store)
    {
        var boundary = Substitute.For<IExclusiveLeaseStore>();
        boundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => store.TryAcquireAsync(call.Arg<LeaseResource>(), call.Arg<string>(), call.Arg<TimeSpan>(), call.Arg<CancellationToken>()));
        boundary.TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>())
            .Returns(call => store.TryReleaseAsync(call.Arg<LeaseIdentity>(), call.Arg<CancellationToken>()));
        return boundary;
    }
}
