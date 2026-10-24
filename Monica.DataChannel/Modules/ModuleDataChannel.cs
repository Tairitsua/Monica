using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.DataChannel;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Facades;
using Monica.DataChannel.Metrics;
using Monica.DataChannel.Providers.Kafka;
using Monica.DataChannel.Providers.TCP.Utils;
using Monica.DataChannel.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Registers host-scoped data-channel composition, management, initialization, and endpoints.
/// </summary>
public class ModuleDataChannel : MonicaModule<ModuleDataChannelOption>, IWebHostRequiredModule
{
    internal const string CHANNEL_SETUP_FEATURE = "channel-setup";

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
        module.RequireFeature(CHANNEL_SETUP_FEATURE);
    }

    /// <inheritdoc />
    public override void ValidateOptions(ModuleDataChannelOption options, string? profileName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.RecentExceptionToKeep);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.InitThreadCount);
    }

    public override void ConfigureServices(ModuleContext<ModuleDataChannelOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<DataChannelRuntime>();
        services.TryAddSingleton<IDataChannelRegistrar>(provider => provider.GetRequiredService<DataChannelRuntime>());
        services.TryAddSingleton<IDataChannelManager>(provider => provider.GetRequiredService<DataChannelRuntime>());
        services.TryAddSingleton<TcpConnectionRuntime>();
        services.TryAddSingleton<IKafkaDataChannelClientFactory, KafkaDataChannelClientFactory>();
        services.TryAddSingleton<MessageMetrics>();
        services.AddScoped<DataChannelFacade>();
        services.AddHostedService<DataChannelInitializerService>();
        // Every setup is admitted independently before construction. This coordinator only sees admitted channels.
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ImplementationType == typeof(DataChannelInitializerService)), typeof(DataChannelInitializerService));
    }

    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleDataChannelOption> context)
    {
        var app = context.ApplicationBuilder;
        var setups = app.ApplicationServices.GetServices<DataChannelSetupRegistration>();
        var registrar = app.ApplicationServices.GetRequiredService<IDataChannelRegistrar>();
        var runtime = app.ApplicationServices.GetRequiredService<DataChannelRuntime>();

        foreach (var setup in setups.Where(setup => setup.Mode == ActivationMode.Automatic))
        {
            runtime.ApplySetup((IDataChannelSetup)app.ApplicationServices.GetRequiredService(setup.ImplementationType),
                setup.RegistrationOwner, registrar);
        }

        runtime.Materialize(app);
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleDataChannelOption> context)
    {
        var app = context.ApplicationBuilder;
        app.ApplicationServices.GetRequiredService<DataChannelRuntime>().ConfigureEndpoints(app);

        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapPost("/channel/{id}/reinitialize",
                async ([FromRoute] string id,
                      [FromServices] DataChannelFacade service,
                      CancellationToken cancellationToken = default) =>
                {
                    var result = await service.ReInitializeChannelAsync(id, cancellationToken);
                    return result.GetResponse();
                })
                .WithName("DataChannel.Reinitialize")
                .WithTags(tagName)
                .WithSummary("Reinitialize a data channel")
                .WithDescription("Reinitializes the data channel with the specified identifier.");

            endpoints.MapGet("/channels",
                async ([FromServices] DataChannelFacade service) =>
                {
                    var result = await service.GetChannelsStatusAsync();
                    return result.GetResponse();
                })
                .WithName("DataChannel.List")
                .WithTags(tagName)
                .WithSummary("List data-channel status")
                .WithDescription("Returns a status snapshot for every data channel owned by the current host.");

            endpoints.MapGet("/channel/{id}/exceptions",
                async ([FromRoute] string id,
                      [FromQuery] int count,
                      [FromServices] DataChannelFacade service) =>
                {
                    var result = await service.GetChannelExceptionsAsync(id, count);
                    return result.GetResponse();
                })
                .WithName("DataChannel.GetExceptions")
                .WithTags(tagName)
                .WithSummary("Get data-channel exceptions")
                .WithDescription("Returns recent exception records for the specified data channel.");

            endpoints.MapGet("/channels/exceptions/summary",
                async ([FromServices] DataChannelFacade service) =>
                {
                    var result = await service.GetExceptionSummaryAsync();
                    return result.GetResponse();
                })
                .WithName("DataChannel.GetExceptionSummary")
                .WithTags(tagName)
                .WithSummary("Get the data-channel exception summary")
                .WithDescription("Returns aggregate exception statistics for data channels owned by the current host.");

            endpoints.MapDelete("/channel/{id}/exceptions",
                async ([FromRoute] string id,
                      [FromServices] DataChannelFacade service) =>
                {
                    var result = await service.ClearChannelExceptionsAsync(id);
                    return result.GetResponse();
                })
                .WithName("DataChannel.ClearExceptions")
                .WithTags(tagName)
                .WithSummary("Clear retained data-channel history")
                .WithDescription("Clears all retained observable records, including ordinary records and exceptions, while preserving lifetime counters.");
        });
    }

}

public static class ModuleDataChannelBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Adds the DataChannel module to the current Monica host.
        /// </summary>
        /// <param name="action">An optional callback that configures retention, initialization, and Minimal API options.</param>
        /// <returns>The host-bound DataChannel registration.</returns>
        public ModuleRegistration<ModuleDataChannel, ModuleDataChannelOption> AddDataChannel(
            Action<ModuleDataChannelOption>? action = null)
        {
            return builder.AddModule<ModuleDataChannel, ModuleDataChannelOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleDataChannel, ModuleDataChannelOption> registration)
    {
        /// <summary>
        /// Adds a singleton setup that contributes pipelines owned by the current host.
        /// </summary>
        /// <typeparam name="TSetup">The host-owned pipeline setup.</typeparam>
        /// <returns>The same host-bound registration.</returns>
        /// <remarks>
        /// Setups run once per implementation type in registration order before any pipeline is materialized.
        /// Repeated calls for the same setup type have no additional effect. Channel identifiers must be unique
        /// across every setup contributed to the host.
        /// </remarks>
        public ModuleRegistration<ModuleDataChannel, ModuleDataChannelOption> UseSetup<TSetup>()
            where TSetup : class, IDataChannelSetup
        {
            return registration
                .ConfigureServices(context =>
                {
                    var existing = context.Services.Where(descriptor => descriptor.ServiceType == typeof(DataChannelSetupRegistration))
                        .Select(descriptor => (DataChannelSetupRegistration)descriptor.ImplementationInstance!)
                        .SingleOrDefault(setup => setup.ImplementationType == typeof(TSetup));
                    if (existing is not null)
                    {
                        if (existing.RegistrationOwner != context.RegistrationOwner)
                            throw new InvalidOperationException($"Data-channel setup '{typeof(TSetup).FullName}' has conflicting registration owners.");
                        return;
                    }
                    var mode = context.Application.TypeActivation!.GetActivation(
                        typeof(TSetup), TypeActivationKind.DataChannel, context.RegistrationOwner);
                    context.Services.AddSingleton(new DataChannelSetupRegistration(typeof(TSetup), context.RegistrationOwner, mode));
                    context.Application.RecordActivation(new ResourceActivation
                    {
                        Key = $"DataChannel:setup:{typeof(TSetup).FullName}",
                        ImplementationType = typeof(TSetup),
                        RegistrationOwner = context.RegistrationOwner,
                        Kind = TypeActivationKind.DataChannel,
                        Mode = mode,
                        Reason = mode == ActivationMode.Automatic ? "Setup may declare and materialize channels."
                            : "Setup is held before construction; its channel factories and endpoints are not materialized."
                    });
                    if (mode == ActivationMode.Automatic)
                    {
                        context.Services.TryAddSingleton<TSetup>();
                        context.Services.AddSingleton<IDataChannelSetup>(provider => provider.GetRequiredService<TSetup>());
                    }
                })
                .SatisfyFeature(ModuleDataChannel.CHANNEL_SETUP_FEATURE);
        }
    }
}

/// <summary>
/// Configuration options for the DataChannel module.
/// Defines host-specific settings and module-level behavior for data channels.
/// </summary>
public class ModuleDataChannelOption : MinimalApiModuleOptions<ModuleDataChannel>
{
    /// <summary>
    /// Gets or sets each channel's shared observable history capacity, including ordinary records and exceptions.
    /// The default is 10 and the value must be positive; lifetime counters survive trimming and clearing.
    /// </summary>
    public int RecentExceptionToKeep { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum number of channels initialized concurrently. The default is 10 and the value must be positive.
    /// </summary>
    public int InitThreadCount { get; set; } = 10;
}
