using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Locker.Abstractions;
using Monica.Locker.Models;
using Monica.Modules;
using Test.Monica.Locker.Support;
using Xunit;

namespace Test.Monica.Locker.Services;

public sealed class DistributedLockServiceTests
{
    [Fact]
    public async Task TryAcquireAsync_WhenOptionsAreOmitted_ShouldApplySharedPrefixAndConfiguredWait()
    {
        var provider = new RecordingLockProvider();
        await using var app = await CreateFactory(provider).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var locker = app.Services.GetRequiredService<IDistributedLock>();

        await using var handle = await locker.TryAcquireAsync("resource:17", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(handle);
        var call = Assert.Single(provider.Calls);
        Assert.Equal("shared:resource:17", call.Key);
        Assert.Equal(TimeSpan.FromSeconds(17), call.Options.WaitTimeout);
        Assert.Null(call.Options.Owner);
        Assert.Null(call.Options.LeaseDuration);
        Assert.Equal(TestContext.Current.CancellationToken, call.CancellationToken);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenCallerSuppliesOptions_ShouldRetainExplicitWaitOwnerAndProviderLease()
    {
        var provider = new RecordingLockProvider();
        await using var app = await CreateFactory(provider).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var options = new LockAcquisitionOptions
        {
            Owner = "logical-owner", WaitTimeout = TimeSpan.Zero, LeaseDuration = TimeSpan.FromSeconds(3)
        };

        await using var handle = await app.Services.GetRequiredService<IDistributedLock>()
            .TryAcquireAsync("resource:17", options, TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal(TimeSpan.Zero, call.Options.WaitTimeout);
        Assert.Equal(options.Owner, call.Options.Owner);
        Assert.Equal(options.LeaseDuration, call.Options.LeaseDuration);
        Assert.NotSame(options, call.Options);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenAcquisitionTimesOut_ShouldReturnNull()
    {
        var provider = new RecordingLockProvider { Acquire = (_, _, _) => Task.FromResult<IDistributedLockHandle?>(null) };
        await using var app = await CreateFactory(provider).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var handle = await app.Services.GetRequiredService<IDistributedLock>()
            .TryAcquireAsync("resource", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(handle);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenWaitingIsCanceled_ShouldPropagateCancellation()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingLockProvider
        {
            Acquire = async (_, _, cancellationToken) =>
            {
                admitted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
        };
        await using var app = await CreateFactory(provider).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = app.Services.GetRequiredService<IDistributedLock>().TryAcquireAsync("resource", cancellationToken: cancellation.Token);
        await admitted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, Assert.Single(provider.Calls).CancellationToken);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenBackendFails_ShouldPreserveFailureWithoutFallback()
    {
        var failure = new IOException("lock backend unavailable");
        var provider = new RecordingLockProvider { Acquire = (_, _, _) => Task.FromException<IDistributedLockHandle?>(failure) };
        await using var app = await CreateFactory(provider).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var observed = await Assert.ThrowsAsync<IOException>(() => app.Services.GetRequiredService<IDistributedLock>()
            .TryAcquireAsync("resource", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Same(failure, observed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TryAcquireAsync_WhenResourceNameIsEmpty_ShouldRejectBeforeCallingProvider(string? resource)
    {
        var provider = new RecordingLockProvider();
        await using var app = await CreateFactory(provider).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => app.Services.GetRequiredService<IDistributedLock>()
            .TryAcquireAsync(resource!, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(provider.Calls);
    }

    private static LockerTestApplicationFactory CreateFactory(RecordingLockProvider provider)
        => new(builder => builder.AddLocker(options =>
        {
            options.LockKeyPrefix = "shared:";
            options.DefaultWaitTimeout = TimeSpan.FromSeconds(17);
        }).UseProvider<RecordingLockProvider>(), services =>
        {
            services.RemoveAll<ILockProvider>();
            services.AddSingleton<ILockProvider>(provider);
        });

    public sealed class RecordingLockProvider : ILockProvider
    {
        public List<Acquisition> Calls { get; } = [];
        public Func<string, LockAcquisitionOptions, CancellationToken, Task<IDistributedLockHandle?>> Acquire { get; init; }
            = (_, _, _) => Task.FromResult<IDistributedLockHandle?>(new RecordingHandle());

        public Task<IDistributedLockHandle?> TryAcquireAsync(string normalizedKey, LockAcquisitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Acquisition(normalizedKey, options, cancellationToken));
            return Acquire(normalizedKey, options, cancellationToken);
        }
    }

    public sealed record Acquisition(string Key, LockAcquisitionOptions Options, CancellationToken CancellationToken);

    private sealed class RecordingHandle : IDistributedLockHandle
    {
        public CancellationToken HandleLostToken => CancellationToken.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
