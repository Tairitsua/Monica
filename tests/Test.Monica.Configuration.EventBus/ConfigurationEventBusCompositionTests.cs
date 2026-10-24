using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Configuration.EventBus.Modules;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EventBus.Services;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Providers.NoOp;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Configuration.EventBus;

public sealed class ConfigurationEventBusCompositionTests
{
    [Fact]
    public void AddMonica_WhenManual_ShouldRetainExplicitNotificationsWithoutStartingASubscription()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddKeyedSingleton<IDistributedEventBus, NoOpDistributedEventBus>("configuration");
        builder.AddMonica(monica =>
        {
            monica.ConfigureApplication(options => options.AutonomousWork = false);
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModule<ModuleConfigurationEventBus, ModuleConfigurationEventBusOption>(options =>
                options.DistributedEventBusServiceKey = "configuration");
        });

        builder.Services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ConfigurationEventBusSubscriptionHostedService));
        builder.Services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IConfigurationChangeNotifier));
    }

    [Fact]
    public void AddMonica_WhenDefaultDistributedEventBusIsMissing_ShouldRejectComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModule<ModuleConfigurationEventBus, ModuleConfigurationEventBusOption>();
        });

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*ModuleConfigurationEventBus*unkeyed IDistributedEventBus*");
    }

    [Fact]
    public void AddMonica_WhenHostOwnsSelectedKeyedDistributedEventBus_ShouldCompose()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddKeyedSingleton<IDistributedEventBus, NoOpDistributedEventBus>("configuration");

        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModule<ModuleConfigurationEventBus, ModuleConfigurationEventBusOption>(options =>
                options.DistributedEventBusServiceKey = "configuration");
        });

        using var host = builder.Build();
        host.Services.GetRequiredKeyedService<IDistributedEventBus>("configuration")
            .Should().BeOfType<NoOpDistributedEventBus>();
    }
}
