using Dapr.Client;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.JsonSerialization.Extensions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Dapr.Abstractions;
using Monica.Dapr.Providers;
using Monica.Dapr.Services;
using Monica.HealthCheck.Extensions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleDaprClientBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the Dapr client, sidecar health monitoring, and optional sidecar metadata endpoint.
        /// </summary>
        /// <param name="action">Optional host-owned Dapr client configuration.</param>
        /// <returns>The host-bound Dapr client module registration.</returns>
        public ModuleRegistration<ModuleDaprClient, ModuleDaprClientOption> AddDaprClient(Action<ModuleDaprClientOption>? action = null)
        {
            return builder.AddModule<ModuleDaprClient, ModuleDaprClientOption>(action);
        }
    }
}

/// <summary>
/// Owns the Dapr SDK client, sidecar health monitoring, and the optional sidecar metadata endpoint.
/// </summary>
public sealed class ModuleDaprClient : MonicaModule<ModuleDaprClientOption>, IWebModule
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleDaprClientOption> context)
    {
        var services = context.Services;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRemoteExceptionDiagnosticsExtractor,
            DaprRemoteExceptionDiagnosticsExtractor>());
        services.RemoveAll<DaprClient>();
        services.AddDaprClient(builder => builder.UseGrpcChannelOptions(new GrpcChannelOptions()
        {
            MaxReceiveMessageSize = Option.GrpcMaxReceiveMessageSizeBytes,
            MaxSendMessageSize = Option.GrpcMaxSendMessageSizeBytes,
            MaxRetryBufferSize = Option.GrpcMaxRetryBufferSizeBytes,
        }).UseJsonSerializationOptions(services.GetMonicaJsonSerializerOptions()));

        // Register health coordinator (singleton implementing both interface and IHostedService)
        services.AddSingleton<DaprSidecarHealthCoordinator>();
        services.AddSingleton<IDaprSidecarHealthCoordinator>(sp =>
            sp.GetRequiredService<DaprSidecarHealthCoordinator>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<DaprSidecarHealthCoordinator>());

        services.AddHealthChecks()
            .AddMonicaReadinessCheck<DaprSidecarHealthCheck>(
                "monica.dapr-sidecar",
                tags: ["dapr"]);
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleDaprClientOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();
            endpoints.MapGet(
                    "/dapr/metadata",
                    static ([FromServices] DaprClient daprClient, CancellationToken cancellationToken) =>
                        daprClient.GetMetadataAsync(cancellationToken))
                .WithName("GetDaprSidecarMetadata")
                .WithTags(tagName)
                .WithSummary("Gets metadata reported by the Dapr sidecar.")
                .WithDescription("Returns the metadata currently reported by the Dapr sidecar connected to this host.");
        });
    }

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleHealthCheck, ModuleHealthCheckOption>();
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
    }
}

/// <summary>
/// Configures the host-owned Dapr SDK client, sidecar health policy, and metadata endpoint.
/// </summary>
public sealed class ModuleDaprClientOption : MinimalApiModuleOptions<ModuleDaprClient>
{
    /// <summary>
    /// Gets or sets the maximum gRPC message size, in bytes, that the client can send.
    /// Sending a larger message throws an exception. The default is 104,857,600 bytes (100 MiB).
    /// <para>
    /// Set this to <see langword="null" /> to remove the client-side send limit.
    /// </para>
    /// </summary>
    public int? GrpcMaxSendMessageSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum gRPC message size, in bytes, that the client can receive.
    /// Receiving a larger message throws an exception. The default is 104,857,600 bytes (100 MiB).
    /// <para>
    /// Set this to <see langword="null" /> to remove the client-side receive limit. This setting does not configure
    /// ASP.NET Core request-body limits or HTTP response-header limits.
    /// </para>
    /// </summary>
    public int? GrpcMaxReceiveMessageSizeBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum gRPC retry buffer size, in bytes, shared across calls on the channel.
    /// If the buffer is exhausted, no additional retry attempts are made and all but one hedging call are canceled.
    /// The default is 104,857,600 bytes (100 MiB).
    /// <para>
    /// Setting this value alone does not enable retries. Retries are enabled in the service config, which can be done
    /// using <see cref="P:Grpc.Net.Client.GrpcChannelOptions.ServiceConfig" />.
    /// </para>
    /// <para>
    /// Set this to <see langword="null" /> to remove the retry buffer limit.
    /// </para>
    /// </summary>
    public long? GrpcMaxRetryBufferSizeBytes { get; set; } = 100 * 1024 * 1024;

    // Health Check Options

    /// <summary>
    /// Gets or sets the interval between sidecar health checks after the first successful check.
    /// The default is 30 seconds.
    /// </summary>
    public TimeSpan PeriodicCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the number of sidecar health-check attempts during initial startup.
    /// The default is 10 attempts.
    /// </summary>
    public int InitialRetryTimes { get; set; } = 10;

    /// <summary>
    /// Gets or sets the initial delay between startup health-check attempts.
    /// The default is two seconds.
    /// </summary>
    public TimeSpan InitialRetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum health-check retry delay after exponential backoff.
    /// The default is 30 seconds.
    /// </summary>
    public TimeSpan MaxRetryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the exponential multiplier applied to health-check retry delays.
    /// The default is 1.5.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 1.5;

    /// <summary>
    /// Gets or sets the number of consecutive health-check failures that marks the sidecar as degraded.
    /// The default is two failures.
    /// </summary>
    public int DegradedThreshold { get; set; } = 2;

    /// <summary>
    /// Gets or sets the number of consecutive health-check failures that marks the sidecar as unhealthy.
    /// The default is five failures.
    /// </summary>
    public int UnhealthyThreshold { get; set; } = 5;

    /// <summary>
    /// Gets or sets the consecutive health-check failure threshold that requests application shutdown when
    /// <see cref="EnableFailFast" /> is enabled. The threshold applies during startup and periodic checks.
    /// The default is 10 failures.
    /// </summary>
    public int FailFastThreshold { get; set; } = 10;

    /// <summary>
    /// Gets or sets whether repeated sidecar health-check failures request graceful application shutdown through
    /// <see cref="IHostApplicationLifetime.StopApplication" />. The default is <see langword="false" />, which keeps
    /// the application running with degraded or unhealthy sidecar state.
    /// </summary>
    public bool EnableFailFast { get; set; }
}
