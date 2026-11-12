using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Dapr.Services;
using Monica.Locker.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Dapr.Modules;

public sealed class ModuleDaprLockerCompositionTests
{
    [Fact]
    public async Task CreateAsync_WhenDaprLockerUsesScopeValidation_ShouldResolveSingletonsAcrossScopes()
    {
        await using var application = await new DaprLockerApplicationFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var provider = application.Services.GetRequiredService<ILockProvider>();
        var locker = application.Services.GetRequiredService<IDistributedLock>();

        Assert.IsType<DaprLockProvider>(provider);

        await using (var firstScope = application.CreateScope(TestContext.Current.CancellationToken))
        {
            Assert.Same(provider, firstScope.Resolve<ILockProvider>());
            Assert.Same(locker, firstScope.Resolve<IDistributedLock>());
        }

        await using var secondScope = application.CreateScope(TestContext.Current.CancellationToken);
        Assert.Same(provider, secondScope.Resolve<ILockProvider>());
        Assert.Same(locker, secondScope.Resolve<IDistributedLock>());
    }

    [Fact]
    public async Task CreateAsync_WhenTwoDaprLockerHostsCoexist_ShouldIsolateSingletons()
    {
        var factory = new DaprLockerApplicationFactory();
        await using var firstHost = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var secondHost = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotSame(firstHost.Application, secondHost.Application);
        Assert.NotSame(
            firstHost.Services.GetRequiredService<ILockProvider>(),
            secondHost.Services.GetRequiredService<ILockProvider>());
        Assert.NotSame(
            firstHost.Services.GetRequiredService<IDistributedLock>(),
            secondHost.Services.GetRequiredService<IDistributedLock>());
    }

    private sealed class DaprLockerApplicationFactory : MonicaTestApplicationFactory<ModuleDaprLockerCompositionTests>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddLocker().UseDaprProvider(options =>
            {
                options.StoreName = "test-lock-store";
                options.DaprHttpEndpoint = "http://127.0.0.1:3500";
            });
        }
    }
}
