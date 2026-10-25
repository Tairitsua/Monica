using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Providers.NoOp;
using Monica.EventBus.Services;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Framework.UI.UIEventBus.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.UI.EventBus;

public sealed class ModuleEventBusUICompositionTests
{
    [Fact]
    public async Task TransitiveComposition_WithoutDistributedProvider_ShouldAllowLocalDiagnostics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModule<EventBusUIConsumerModule, EventBusUIConsumerModuleOption>();
        });
        await using var app = builder.Build();
        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<EventBusTestService>().Should().NotBeNull();
        var providers = scope.ServiceProvider.GetRequiredService<EventBusProviderDiscoveryService>().GetRegisteredProviders();
        providers.Status.Should().Be(ResStatus.Ok);
        providers.Message.Should().BeNull();
        providers.Data.Should().ContainSingle(provider => !provider.IsDistributed);
        providers.Data.Should().NotContain(provider => provider.IsDistributed);
    }

    [Fact]
    public void Module_ShouldRetainExplicitUiAndWebRuntimeKinds()
    {
        var module = new ModuleEventBusUI();

        module.Should().BeAssignableTo<IUIModule>();
        module.Should().BeAssignableTo<IWebHostRequiredModule>();
    }

    [Fact]
    public async Task TransitiveComposition_WithDefaultDistributedProvider_ShouldComposeIntrinsicContracts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddEventBus().UseNoOpDistributedEventBus();
            monica.AddModule<EventBusUIConsumerModule, EventBusUIConsumerModuleOption>();
        });

        await using var app = builder.Build();
        var application = app.Services.GetRequiredService<MonicaApplication>();
        var dependencies = GetDirectDependencyTypes(application, typeof(ModuleEventBusUI));

        dependencies.Should().Contain(typeof(ModuleEventBus));
        dependencies.Should().Contain(typeof(ModuleResultEnvelope));
        dependencies.Should().Contain(typeof(ModuleLocalization));
        dependencies.Should().Contain(typeof(ModuleShellUI));
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDistributedEventBus>().Should()
            .BeOfType<ScopedDistributedEventBusGateway>();
        app.Services.GetRequiredService<NoOpDistributedEventBus>().Should().NotBeNull();
        app.Services.GetRequiredService<IJsonSerializerOptionsProvider>().Should().NotBeNull();
    }

    private static IReadOnlySet<Type> GetDirectDependencyTypes(
        MonicaApplication application,
        Type moduleType)
    {
        var moduleKey = application.Dependencies.ModuleKeysByType[moduleType];
        return application.Dependencies.DependenciesByModule[moduleKey]
            .Select(dependency => application.Dependencies.ModuleTypesByKey[dependency])
            .ToHashSet();
    }
}

public sealed class EventBusUIConsumerModule : MonicaModule<EventBusUIConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleEventBusUI, ModuleEventBusUIOption>();
    }
}

public sealed class EventBusUIConsumerModuleOption : ModuleOptions<EventBusUIConsumerModule>;
