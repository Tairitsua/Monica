using Monica.Locker.Models;
using Monica.Locker.Providers.InProcess;
using Xunit;

namespace Test.Monica.Locker.Providers.InProcess;

public sealed class InProcessLockProviderTests
{
    [Fact]
    public async Task TryAcquireAsync_WhenSameResourceIsHeld_ShouldExcludeContenderUntilRelease()
    {
        var provider = new InProcessLockProvider();
        var first = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.False(first.HandleLostToken.CanBeCanceled);
        try
        {
            var contender = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
            Assert.Null(contender);
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using var next = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenResourcesDiffer_ShouldPermitIndependentOwnership()
    {
        var provider = new InProcessLockProvider();
        await using var first = await provider.TryAcquireAsync("resource-a", Immediate(), TestContext.Current.CancellationToken);
        await using var second = await provider.TryAcquireAsync("resource-b", Immediate(), TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenWaiterIsCanceled_ShouldNotAcquireAfterOwnerReleases()
    {
        var provider = new InProcessLockProvider();
        var owner = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
        Assert.NotNull(owner);
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pending = provider.TryAcquireAsync("resource", new LockAcquisitionOptions
            {
                WaitTimeout = Timeout.InfiniteTimeSpan
            }, cancellation.Token);
            Assert.False(pending.IsCompleted);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally
        {
            await owner.DisposeAsync();
        }

        await using var next = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenProtectedOperationThrows_ShouldReleaseThroughHandleDisposal()
    {
        var provider = new InProcessLockProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var handle = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
            Assert.NotNull(handle);
            throw new InvalidOperationException("protected operation failed");
        });

        await using var next = await provider.TryAcquireAsync("resource", Immediate(), TestContext.Current.CancellationToken);
        Assert.NotNull(next);
    }

    private static LockAcquisitionOptions Immediate() => new() { WaitTimeout = TimeSpan.Zero };
}
