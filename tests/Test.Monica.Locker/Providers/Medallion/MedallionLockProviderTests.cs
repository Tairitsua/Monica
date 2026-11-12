using Monica.Locker.Models;
using Monica.Locker.Providers.Medallion;
using NSubstitute;
using Xunit;
using NativeHandle = Medallion.Threading.IDistributedSynchronizationHandle;
using NativeLock = Medallion.Threading.IDistributedLock;
using NativeProvider = Medallion.Threading.IDistributedLockProvider;

namespace Test.Monica.Locker.Providers.Medallion;

public sealed class MedallionLockProviderTests
{
    [Fact]
    public async Task TryAcquireAsync_WhenNativeHandleIsLost_ShouldForwardExactSignalAndReleaseNativeHandle()
    {
        using var lost = new CancellationTokenSource();
        var nativeHandle = Substitute.For<NativeHandle>();
        nativeHandle.HandleLostToken.Returns(lost.Token);
        var nativeLock = Substitute.For<NativeLock>();
        nativeLock.TryAcquireAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<NativeHandle?>(nativeHandle));
        var nativeProvider = Substitute.For<NativeProvider>();
        nativeProvider.CreateLock("shared:resource").Returns(nativeLock);
        var provider = new MedallionLockProvider(nativeProvider);
        var handle = await provider.TryAcquireAsync("shared:resource", new LockAcquisitionOptions
        {
            WaitTimeout = TimeSpan.FromSeconds(7)
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(handle);
        try
        {
            Assert.Equal(lost.Token, handle.HandleLostToken);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken, handle.HandleLostToken);
            var pendingOperation = Task.Delay(Timeout.InfiniteTimeSpan, operation.Token);

            await lost.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingOperation);
            await nativeLock.Received(1).TryAcquireAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);
        }
        finally
        {
            await handle.DisposeAsync();
        }

        await nativeHandle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenNativeAcquisitionTimesOut_ShouldReturnNull()
    {
        var nativeLock = Substitute.For<NativeLock>();
        nativeLock.TryAcquireAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<NativeHandle?>((NativeHandle?)null));
        var nativeProvider = Substitute.For<NativeProvider>();
        nativeProvider.CreateLock("resource").Returns(nativeLock);

        var handle = await new MedallionLockProvider(nativeProvider).TryAcquireAsync("resource",
            new LockAcquisitionOptions { WaitTimeout = TimeSpan.Zero }, TestContext.Current.CancellationToken);

        Assert.Null(handle);
    }

    [Fact]
    public async Task DisposeAsync_WhenNativeReleaseFails_ShouldPropagateFailure()
    {
        var failure = new IOException("native release failed");
        var nativeHandle = Substitute.For<NativeHandle>();
        nativeHandle.DisposeAsync().Returns(ValueTask.FromException(failure));
        var nativeLock = Substitute.For<NativeLock>();
        nativeLock.TryAcquireAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<NativeHandle?>(nativeHandle));
        var nativeProvider = Substitute.For<NativeProvider>();
        nativeProvider.CreateLock("resource").Returns(nativeLock);
        var handle = await new MedallionLockProvider(nativeProvider).TryAcquireAsync("resource",
            new LockAcquisitionOptions(), TestContext.Current.CancellationToken);
        Assert.NotNull(handle);

        var observed = await Assert.ThrowsAsync<IOException>(async () => await handle.DisposeAsync());

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenNativeAcquisitionIsCanceled_ShouldPropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var nativeLock = Substitute.For<NativeLock>();
        nativeLock.TryAcquireAsync(Arg.Any<TimeSpan>(), cancellation.Token)
            .Returns(ValueTask.FromCanceled<NativeHandle?>(cancellation.Token));
        var nativeProvider = Substitute.For<NativeProvider>();
        nativeProvider.CreateLock("resource").Returns(nativeLock);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MedallionLockProvider(nativeProvider).TryAcquireAsync("resource", new LockAcquisitionOptions(), cancellation.Token));
    }
}
