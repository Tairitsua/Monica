using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EventBus.Services;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.EventBus.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;

namespace Monica.Configuration.EventBus.Modules;

/// <summary>
/// Bridges Monica.Configuration change notifications to the distributed EventBus.
/// </summary>
public sealed class ModuleConfigurationEventBus : MonicaModule<ModuleConfigurationEventBusOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleConfigurationEventBusOption> context)
    {
        var services = context.Services;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigurationChangeNotifier, ConfigurationEventBusChangeNotifier>());
        if (Application.Application.AutonomousWork)
            services.AddHostedService<ConfigurationEventBusSubscriptionHostedService>();
        Application.RecordActivation(new ResourceActivation
        {
            Key = "configuration:eventbus-subscription",
            ImplementationType = typeof(ConfigurationEventBusSubscriptionHostedService),
            Kind = TypeActivationKind.ExternalSubscription,
            Mode = Application.Application.AutonomousWork ? ActivationMode.Automatic : ActivationMode.Manual,
            Reason = Application.Application.AutonomousWork ? "Configuration reload notifications are subscribed automatically."
                : "Automatic configuration reload subscriptions are disabled; explicit edits may publish invalidation metadata."
        });
    }

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleConfiguration, ModuleConfigurationOption>();
        module.Require<ModuleEventBus, ModuleEventBusOption>();
    }

    /// <inheritdoc />
    public override void DeclareContracts(ModuleContractDescriptor<ModuleConfigurationEventBusOption> contracts)
    {
        if (string.IsNullOrWhiteSpace(contracts.Options.DistributedEventBusServiceKey))
        {
            contracts.RequireService<IDistributedEventBus>();
            return;
        }

        contracts.RequireKeyedService<IDistributedEventBus>(contracts.Options.DistributedEventBusServiceKey);
    }
}

/// <summary>
/// Options for the Monica.Configuration EventBus bridge module.
/// </summary>
public sealed class ModuleConfigurationEventBusOption : ModuleOptions<ModuleConfigurationEventBus>
{
    /// <summary>
    /// Gets or sets the distributed EventBus topic used for configuration reload signals.
    /// </summary>
    /// <remarks>
    /// The message payload contains only invalidation metadata, never configuration values.
    /// All participating services must use the same topic name.
    /// </remarks>
    public string TopicName { get; set; } = "monica.configuration.reload";

    /// <summary>
    /// Gets or sets the keyed distributed EventBus service key used by the bridge.
    /// </summary>
    /// <remarks>
    /// Leave this value <see langword="null"/> to use the default <see cref="IDistributedEventBus"/>.
    /// Set it when the host registers multiple distributed EventBus providers and configuration reload
    /// signals should travel through a dedicated provider.
    /// </remarks>
    public string? DistributedEventBusServiceKey { get; set; }
}
