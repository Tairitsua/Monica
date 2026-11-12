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
        services.TryAddSingleton<IDataChannelLifecycle>(provider => provider.GetRequiredService<DataChannelRuntime>());
        services.TryAddSingleton<TcpConnectionRuntime>();
        services.TryAddSingleton<IKafkaDataChannelClientFactory, KafkaDataChannelClientFactory>();
        services.TryAddSingleton<MessageMetrics>();
        services.AddScoped<DataChannelFacade>();
        services.AddHostedService(provider => new DataChannelInitializerService(
            provider.GetRequiredService<DataChannelRuntime>(),
            provider.GetRequiredService<Monica.Core.ObservableInstance.Abstractions.IObservableInstanceRegistry>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModuleHostedServiceOption>>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DataChannelInitializerService>>()));
        // Every setup is admitted independently before construction. This coordinator only sees admitted channels.
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)), typeof(DataChannelInitializerService));
    }

    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleDataChannelOption> context)
    {
        var app = context.ApplicationBuilder;
        var runtime = app.ApplicationServices.GetRequiredService<DataChannelRuntime>();
        runtime.ConfigureApplication(app);
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleDataChannelOption> context)
    {
        var app = context.ApplicationBuilder;
        app.ApplicationServices.GetRequiredService<DataChannelRuntime>().ConfigureEndpoints(app);

        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/channel-setups", ([FromServices] DataChannelFacade service) =>
                    service.GetSetups().GetResponse())
                .WithName("DataChannel.ListSetups").WithTags(tagName)
                .WithSummary("List data-channel setup lifecycle evidence");

            endpoints.MapPost("/channel-setups/{id}/start", async (string id,
                    [FromServices] DataChannelFacade service, CancellationToken cancellationToken) =>
                    (await service.StartSetupAsync(id, cancellationToken)).GetResponse())
                .WithName("DataChannel.StartSetup").WithTags(tagName)
                .WithSummary("Start one eligible data-channel setup");

            endpoints.MapPost("/channel-setups/{id}/stop", async (string id,
                    [FromServices] DataChannelFacade service, CancellationToken cancellationToken) =>
                    (await service.StopSetupAsync(id, cancellationToken)).GetResponse())
                .WithName("DataChannel.StopSetup").WithTags(tagName)
                .WithSummary("Stop one data-channel setup and release its endpoints");

            endpoints.MapPost("/channel-setups/{id}/restart", async (string id,
                    [FromServices] DataChannelFacade service, CancellationToken cancellationToken) =>
                    (await service.RestartSetupAsync(id, cancellationToken)).GetResponse())
                .WithName("DataChannel.RestartSetup").WithTags(tagName)
                .WithSummary("Restart one data-channel setup using frozen configuration");

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
        /// Automatic setups declare during host composition, before their pipelines are materialized.
        /// Manual setups remain unconstructed until explicitly started through IDataChannelLifecycle.
        /// A successful declaration is retained for subsequent starts and restarts.
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
                    if (mode != ActivationMode.Disabled)
                    {
                        context.Services.TryAddSingleton<TSetup>();
                        if (mode == ActivationMode.Automatic)
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
