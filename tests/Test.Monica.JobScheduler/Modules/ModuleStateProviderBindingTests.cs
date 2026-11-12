using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.Locker.Leases;
using Monica.StateStore.Abstractions;
using Monica.StateStore.Cancellation.Abstractions;
using Monica.StateStore.Cancellation.Services;
using NSubstitute;
using Xunit;

namespace Test.Monica.JobScheduler.Modules;

public sealed class ModuleStateProviderBindingTests
{
    [Fact]
    public void ValidateOptions_WhenServiceDiscoveryStorageIsUnselected_ShouldExplainProviderSelection()
    {
        var act = () => new ModuleServiceDiscovery().ValidateOptions(
            new ModuleServiceDiscoveryOption(),
            profileName: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*UseMemoryStorage*UseDistributedStorage*UseExternalKeyedStorage*");
    }

    [Fact]
    public async Task UseMemoryStorage_ShouldBindServiceDiscoveryDirectlyToMemoryProvider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddServiceDiscovery()
                .AsStandalone()
                .UseMemoryStorage();
        });

        await using var host = builder.Build();
        var memoryStore = host.Services.GetRequiredService<IMemoryStateStore>();
        var serviceDiscoveryStore = host.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery));
        var options = host.Services.GetRequiredService<IOptions<ModuleServiceDiscoveryOption>>().Value;

        serviceDiscoveryStore.Should().BeSameAs(memoryStore);
        options.Role.Should().Be(ServiceDiscoveryRole.Standalone);
        options.StorageMode.Should().Be(ServiceDiscoveryStorageMode.Memory);
    }

    [Fact]
    public async Task UseExternalKeyedStorage_ShouldPreserveTheExplicitKeyedProvider()
    {
        const string externalKey = "external-service-discovery";
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddStateStore().AddKeyedCommonStateStore(externalKey)
                .ConfigureServices(context => context.Services.AddKeyedSingleton<IExclusiveLeaseStore>(externalKey,
                    new InMemoryLeaseStore(TimeProvider.System)));
            monica.AddServiceDiscovery()
                .AsRegistry()
                .UseExternalKeyedStorage(externalKey)
                .UseLeaderLeaseStore(externalKey, "test-discovery");
        });

        await using var host = builder.Build();
        var externalStore = host.Services.GetRequiredKeyedService<IStateStore>(externalKey);
        var serviceDiscoveryStore = host.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleServiceDiscovery));

        serviceDiscoveryStore.Should().BeSameAs(externalStore);
    }

    [Fact]
    public async Task AddTaskProgress_ShouldBindItsMemoryProvidersWithoutAmbientFallbacks()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddTaskProgress();
        });

        await using var host = builder.Build();
        var memoryStore = host.Services.GetRequiredService<IMemoryStateStore>();
        var taskProgressStore = host.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleTaskProgress));
        var cancellationManager = host.Services.GetRequiredKeyedService<ICancellationManager>(nameof(ModuleTaskProgress));
        var options = host.Services.GetRequiredService<IOptions<ModuleTaskProgressOption>>().Value;

        taskProgressStore.Should().BeSameAs(memoryStore);
        cancellationManager.Should().BeOfType<InMemoryCancellationManager>();
        options.StorageMode.Should().Be(TaskProgressStorageMode.Memory);
    }

    [Fact]
    public async Task UseDistributedStorage_ShouldBindTaskProgressDirectlyToDistributedProvider()
    {
        var distributedStore = Substitute.For<IDistributedStateStore>();
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddStateStore()
                .ConfigureStateStoreServices(services => services.AddSingleton(distributedStore))
                .SatisfyFeature(ModuleStateStore.DISTRIBUTED_PROVIDER_FEATURE);
            monica.AddTaskProgress().UseDistributedStorage();
        });

        await using var host = builder.Build();
        var taskProgressStore = host.Services.GetRequiredKeyedService<IStateStore>(nameof(ModuleTaskProgress));
        var cancellationManager = host.Services.GetRequiredKeyedService<ICancellationManager>(nameof(ModuleTaskProgress));
        var options = host.Services.GetRequiredService<IOptions<ModuleTaskProgressOption>>().Value;

        taskProgressStore.Should().BeSameAs(distributedStore);
        cancellationManager.Should().BeOfType<DistributedCancellationManager>();
        options.StorageMode.Should().Be(TaskProgressStorageMode.Distributed);
    }
}
