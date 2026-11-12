using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Extensions;
using Monica.Locker.Abstractions;
using Monica.Locker.Providers.Medallion;
using Monica.Modules;
using NSubstitute;
using Test.Monica.Locker.Support;
using Xunit;
using NativeLock = Medallion.Threading.IDistributedLock;
using NativeProvider = Medallion.Threading.IDistributedLockProvider;

namespace Test.Monica.Locker.Modules;

public sealed class ModuleLockerCompositionTests
{
    [Fact]
    public void AddLocker_WhenNoProviderIsSelected_ShouldRejectIncompleteComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        var failure = Record.Exception(() => builder.AddMonica(monica => monica.AddLocker()));

        Assert.NotNull(failure);
        Assert.Contains("lock-provider", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseMedallionProvider_WhenFactoryCreatesNativeProviders_ShouldOwnOneSingletonPerHost()
    {
        var created = new List<DisposableNativeProvider>();
        var factory = new LockerTestApplicationFactory(builder => builder.AddLocker().UseMedallionProvider(_ =>
        {
            var provider = new DisposableNativeProvider();
            created.Add(provider);
            return provider;
        }));

        await using (var first = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        await using (var second = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            var firstLocker = first.Services.GetRequiredService<IDistributedLock>();
            var firstProvider = first.Services.GetRequiredService<ILockProvider>();
            var firstNative = first.Services.GetRequiredService<NativeProvider>();
            Assert.IsType<MedallionLockProvider>(firstProvider);
            Assert.NotSame(firstLocker, second.Services.GetRequiredService<IDistributedLock>());
            Assert.NotSame(firstProvider, second.Services.GetRequiredService<ILockProvider>());
            Assert.NotSame(firstNative, second.Services.GetRequiredService<NativeProvider>());
            await using var scope = first.CreateScope(TestContext.Current.CancellationToken);
            Assert.Same(firstLocker, scope.Resolve<IDistributedLock>());
            Assert.Same(firstProvider, scope.Resolve<ILockProvider>());
            Assert.Same(firstNative, scope.Resolve<NativeProvider>());
            Assert.Equal(2, created.Count);
            Assert.All(created, provider => Assert.Equal(0, provider.DisposeCount));
        }

        Assert.All(created, provider => Assert.Equal(1, provider.DisposeCount));
    }

    [Fact]
    public async Task UseMedallionProvider_WhenInstanceIsSupplied_ShouldLeaveDisposalToCaller()
    {
        var native = new DisposableNativeProvider();
        var factory = new LockerTestApplicationFactory(builder => builder.AddLocker().UseMedallionProvider(native));
        await using (var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Same(native, application.Services.GetRequiredService<NativeProvider>());
            Assert.NotNull(application.Services.GetRequiredService<IDistributedLock>());
        }

        Assert.Equal(0, native.DisposeCount);
        await native.DisposeAsync();
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UseMedallionProvider_WhenSingletonFactoryCapturesScopedService_ShouldFailScopeValidation()
    {
        var factory = new LockerTestApplicationFactory(builder => builder.AddLocker().UseMedallionProvider(services =>
        {
            services.GetRequiredService<ScopedDependency>();
            return new DisposableNativeProvider();
        }), services => services.AddScoped<ScopedDependency>());
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => application.Services.GetRequiredService<IDistributedLock>());
    }

    public sealed class ScopedDependency;

    private sealed class DisposableNativeProvider : NativeProvider, IAsyncDisposable
    {
        public int DisposeCount { get; private set; }
        public NativeLock CreateLock(string name) => Substitute.For<NativeLock>();
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
