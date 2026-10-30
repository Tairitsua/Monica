using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Models;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Providers.Local;
using Monica.EventBus.Providers.NoOp;
using Monica.EventBus.Services;
using Monica.EventBus.Services.Support;
using Monica.Tool.Extensions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleEventBusBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the EventBus module.
        /// </summary>
        public ModuleRegistration<ModuleEventBus, ModuleEventBusOption> AddEventBus(Action<ModuleEventBusOption>? action = null)
        {
            return builder.AddModule<ModuleEventBus, ModuleEventBusOption>(action);
        }
    }
}

public class ModuleEventBus : MonicaModule<ModuleEventBusOption>
{
    /// <summary>
    /// Identifies the distributed-provider capability required by distributed EventBus features.
    /// </summary>
    public const string DISTRIBUTED_PROVIDER_FEATURE = "distributed-provider";

    private readonly EventBusAutoDiscovery _autoDiscovery = new();

    public override void ConfigureServices(ModuleContext<ModuleEventBusOption> context)
    {
        var services = context.Services;
        // Register core EventBus services (shared across all EventBus instances)
        services.AddSingleton<IEventSubscriptionRegistry, EventSubscriptionRegistry>();
        services.AddSingleton<ITopicSubscriptionStatusStore, TopicSubscriptionStatusStore>();
        services.AddSingleton<IEventHandlerInvoker, EventHandlerInvoker>();
        services.AddSingleton<LocalEventBus>();
        services.AddScoped<ILocalEventBus>(sp => new ScopedLocalEventBusGateway(
            sp.GetRequiredService<LocalEventBus>(), sp.GetRequiredService<IEventMessageFactory>(), sp));
        services.AddScoped<EventDeliveryContext>();
        services.AddScoped<IEventDeliveryContext>(sp => sp.GetRequiredService<EventDeliveryContext>());
        services.AddSingleton<IEventMessageFactory, EventMessageFactory>();
        services.AddSingleton<IEventReceiveDispatcher, EventReceiveDispatcher>();
        services.AddSingleton<EventMessageRetryPolicy>();
        services.AddSingleton(_autoDiscovery);
        services.AddHostedService<EventBusAutoDiscoveryLifecycle>();
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(EventBusAutoDiscoveryLifecycle)),
            typeof(EventBusAutoDiscoveryLifecycle));
        context.Composition.OnServicesFinalized(static _ => { }, descriptors =>
        {
            if (!Option.DisableAutoDiscovery) _autoDiscovery.CollectRegisteredHandlers(descriptors);
            _autoDiscovery.CaptureRegistrationOwners(descriptors, context.Composition.GetServiceRegistrationOwner);
        });
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>();
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
    }

    /// <inheritdoc />
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleEventBusOption> discovery)
    {
        if (Option.DisableAutoDiscovery)
        {
            return;
        }

        discovery.Match(
            TypeQuery.ConcreteClass.AssignableTo<IEventHandler>(),
            (_, matches) =>
            {
                foreach (var match in matches)
                {
                    try
                    {
                        _autoDiscovery.Collect(match.Type);
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException(
                            $"Failed to register event handler '{match.Type.GetCleanFullName()}': {ex.Message}", ex);
                    }
                }
            });
    }
}

public static class ModuleEventBusRegistrationExtensions
{
    /// <summary>
    /// Registers the shared distributed event bus provider for the default EventBus instance
    /// where <c>ServiceKey</c> is <see langword="null"/>.
    /// </summary>
    public static ModuleRegistration<ModuleEventBus, ModuleEventBusOption> UseDistributedEventBus<TProvider>(this ModuleRegistration<ModuleEventBus, ModuleEventBusOption> module) where TProvider : DistributedEventBusBase
    {
        module.RequireFeature(ModuleEventBus.DISTRIBUTED_PROVIDER_FEATURE);
        return module
            .ConfigureServices(context =>
            {
                context.Services.AddSingleton<TProvider>();
                context.Services.AddSingleton<IEventTransport>(sp =>
                    sp.GetRequiredService<TProvider>() as IEventTransport
                    ?? throw new InvalidOperationException(
                        $"Distributed provider '{typeof(TProvider)}' must implement IEventTransport."));
                context.Services.AddScoped<IDistributedEventBus>(sp => new ScopedDistributedEventBusGateway(
                    sp.GetRequiredService<TProvider>(), sp.GetRequiredService<IEventMessageFactory>(), sp));
            })
            .SatisfyFeature(ModuleEventBus.DISTRIBUTED_PROVIDER_FEATURE);
    }

    /// <summary>
    /// Registers a no-op distributed event bus for testing or scenarios where external event
    /// publishing is not required.
    /// </summary>
    public static ModuleRegistration<ModuleEventBus, ModuleEventBusOption> UseNoOpDistributedEventBus(this ModuleRegistration<ModuleEventBus, ModuleEventBusOption> module)
    {
        module.RequireFeature(ModuleEventBus.DISTRIBUTED_PROVIDER_FEATURE);
        return module
            .ConfigureServices(context =>
            {
                context.Services.AddSingleton<NoOpDistributedEventBus>();
                context.Services.AddScoped<IDistributedEventBus>(sp => new ScopedDistributedEventBusGateway(
                    sp.GetRequiredService<NoOpDistributedEventBus>(),
                    sp.GetRequiredService<IEventMessageFactory>(), sp));
            })
            .SatisfyFeature(ModuleEventBus.DISTRIBUTED_PROVIDER_FEATURE);
    }

    /// <summary>
    /// Registers a keyed <see cref="IEventBus"/> and maps it to either the local or distributed
    /// implementation based on <paramref name="useDistributed"/>.
    /// </summary>
    /// <param name="module">The EventBus module registration.</param>
    /// <param name="key">Service key.</param>
    /// <param name="useDistributed">
    /// <see langword="true"/> to use the distributed event bus; otherwise, the local event bus.
    /// </param>
    public static ModuleRegistration<ModuleEventBus, ModuleEventBusOption> AddKeyedEventBus(this ModuleRegistration<ModuleEventBus, ModuleEventBusOption> module, string key, bool useDistributed = false)
    {
        if (useDistributed)
        {
            module.RequireFeature(ModuleEventBus.DISTRIBUTED_PROVIDER_FEATURE);
        }

        module.ConfigureServices(context =>
        {
            if (useDistributed)
            {
                // For distributed, delegate to the registered keyed IDistributedEventBus
                context.Services.TryAddKeyedScoped<IEventBus>(key, (sp, _) =>
                    sp.GetRequiredService<IDistributedEventBus>());
            }
            else
            {
                // For local, delegate to the registered keyed ILocalEventBus
                context.Services.TryAddKeyedScoped<IEventBus>(key, (sp, _) =>
                    sp.GetRequiredService<ILocalEventBus>());
            }
        });

        module.RecordKeyedServiceKey(key);
        return module;
    }

    /// <summary>
    /// Registers a keyed local event bus backed by a <see cref="LocalEventBus"/> instance that
    /// carries the specified service key.
    /// </summary>
    /// <param name="module">The EventBus module registration.</param>
    /// <param name="key">Service key.</param>
    public static ModuleRegistration<ModuleEventBus, ModuleEventBusOption> AddKeyedLocalEventBus(this ModuleRegistration<ModuleEventBus, ModuleEventBusOption> module, string key)
    {
        module.ConfigureServices(context =>
        {
            // Register keyed LocalEventBus with the specified serviceKey
            context.Services.AddKeyedSingleton<LocalEventBus>(key, (sp, _) =>
                ActivatorUtilities.CreateInstance<LocalEventBus>(sp, key));
            context.Services.AddKeyedScoped<ILocalEventBus>(key, (sp, _) =>
                new ScopedLocalEventBusGateway(sp.GetRequiredKeyedService<LocalEventBus>(key),
                    sp.GetRequiredService<IEventMessageFactory>(), sp, key));
        });

        module.RecordKeyedServiceKey(key);
        return module;
    }

}

public class ModuleEventBusOption : ModuleOptions<ModuleEventBus>
{
    /// <summary>
    /// Gets or sets the number of retries after the first failed distributed receive attempt.
    /// Defaults to one (two attempts in total); zero disables retries. Must be nonnegative.
    /// Dapr and native Kafka apply this budget to all processing failures, including business exceptions,
    /// timeouts, deserialization failures and handler cancellation. Transport shutdown stops processing
    /// without acknowledging the message. Exhaustion logs and ends delivery, forwarding to the provider's
    /// dead-letter destination when configured. Local events and Outbox publishing have separate semantics.
    /// The budget is per transport delivery and resets after broker redelivery or process restart.
    /// </summary>
    public int MessageRetryCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether automatic discovery is disabled for types that
    /// implement <see cref="IDistributedEventHandler{TEvent}"/> or
    /// <see cref="ILocalEventHandler{TEvent}"/>.
    /// </summary>
    /// <remarks>
    /// Automatic subscriptions include only concrete handler types registered in the host container.
    /// When automatic handlers are discovered, the container must expose <see cref="IServiceProviderIsService"/>
    /// so discovery can inspect registrations without activating handlers. Disabled or empty discovery does
    /// not require the inspector. Construction and asynchronous scope disposal occur during delivery.
    /// Distributed handlers retain the application registration owner captured from their finalized service
    /// descriptors. Conflicting owners, including mixed owned and unowned registrations, stop composition.
    /// Concrete closed generic handler types explicitly exposed in DI participate in the same discovery;
    /// open generic templates and interface-only registrations do not identify a concrete subscription.
    /// </remarks>
    public bool DisableAutoDiscovery { get; set; }
}
