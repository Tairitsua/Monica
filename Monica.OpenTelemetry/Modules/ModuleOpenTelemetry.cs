using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.OpenTelemetry.InProcessCollector.Facades;
using Monica.OpenTelemetry.InProcessCollector.Services;
using Monica.OpenTelemetry.Localization;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions used to register Monica's OpenTelemetry SDK wiring module.
/// </summary>
public static class ModuleOpenTelemetryBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers Monica's out-of-the-box OpenTelemetry SDK wiring for metrics exporters and instrumentations.
        /// </summary>
        /// <remarks>
        /// Requires Monica's localization and result envelope modules automatically. The latter also requires
        /// JSON serialization and supplies the host-owned result/JSON contract used by the optional snapshot endpoint.
        /// </remarks>
        /// <param name="action">Optional module option configuration delegate.</param>
        /// <returns>The host-bound OpenTelemetry module registration.</returns>
        public ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> AddOpenTelemetry(Action<ModuleOpenTelemetryOption>? action = null)
        {
            var module = builder.AddModule<ModuleOpenTelemetry, ModuleOpenTelemetryOption>(action);
            module.Require<ModuleLocalization, ModuleLocalizationOption>()
                .AddResource<OpenTelemetryResource>();
            return module;
        }
    }
}

/// <summary>
/// OpenTelemetry integration module that wires metrics exporters, instrumentations, and optional in-process snapshots.
/// </summary>
public class ModuleOpenTelemetry : MonicaModule<ModuleOpenTelemetryOption>, IWebModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization, ModuleLocalizationOption>();
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleOpenTelemetryOption> context)
    {
        var services = context.Services;
        Option.DeploymentEnvironment ??= Application.Application.DeploymentScope;
        if (!Application.Application.AutonomousWork && Option.UseOtlpExporter)
        {
            Option.UseOtlpExporter = false;
            Application.RecordActivation(new ResourceActivation
            {
                Key = "OpenTelemetry:otlp-export",
                Kind = TypeActivationKind.HostedService,
                Mode = ActivationMode.Manual,
                Reason = "External telemetry export is held; in-process observations and local console output remain available."
            });
        }
        var existingHostedServices = services.Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToHashSet();
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => ConfigureResource(resource, Option))
            .WithMetrics(metrics => ConfigureMetrics(metrics, Option));

        if (Option.EnableTracing)
            telemetry.WithTracing(tracing => ConfigureTracing(tracing, Option));
        if (Option.EnableLogging)
        {
            telemetry.WithLogging(logging =>
            {
                if (Option.UseOtlpExporter && !Option.UseOtlpEnvironmentDefaults)
                    logging.AddOtlpExporter(exporter => ConfigureOtlp(exporter, Option));
                if (Option.UseConsoleExporter)
                    logging.AddConsoleExporter();
            }, logging =>
            {
                logging.IncludeFormattedMessage = Option.IncludeFormattedLogMessages;
                logging.IncludeScopes = Option.IncludeLogScopes;
            });
        }
        if (Option.UseOtlpExporter && Option.UseOtlpEnvironmentDefaults)
            telemetry.UseOtlpExporter();

        // The SDK owns configured telemetry, not product background work. Preserve its native lifecycle.
        foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                     && !existingHostedServices.Contains(descriptor)))
        {
            if (descriptor.ImplementationType is { } implementationType)
                context.Composition.AdmitHostedLifecycle(descriptor, implementationType);
        }

        if (!Option.EnableInProcessCollector)
        {
            return;
        }

        services.TryAddSingleton<InProcessMetricsCollector>();
        services.AddHostedService(sp => sp.GetRequiredService<InProcessMetricsCollector>());
        context.Composition.AdmitHostedLifecycle(
            services.Last(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)),
            typeof(InProcessMetricsCollector));
        services.AddScoped<OpenTelemetryFacade>();
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleOpenTelemetryOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            if (Option.UsePrometheusEndpoint)
            {
                endpoints.MapPrometheusScrapingEndpoint(Option.PrometheusEndpointPath)
                    .WithMonicaEndpoint();
            }

            if (!Option.EnableInProcessCollector)
            {
                return;
            }

            endpoints.MapGet(
                    "/opentelemetry/snapshot",
                    async ([FromServices] OpenTelemetryFacade facade) =>
                        (await facade.GetSnapshotAsync()).GetResponse())
                .WithName("Get OpenTelemetry in-process snapshot")
                .WithTags(Option.GetApiGroupName())
                .WithSummary("Returns the in-process metric snapshot collected by Monica.OpenTelemetry.")
                .WithDescription("Returns a per-instance, in-memory snapshot of subscribed meter measurements. Use an exporter for production retention and multi-instance analysis.");
        });
    }

    private static void ConfigureResource(ResourceBuilder resource, ModuleOpenTelemetryOption option)
    {
        resource.AddService(option.GetResourceServiceName(), serviceVersion: option.GetResourceServiceVersion());

        if (!string.IsNullOrWhiteSpace(option.DeploymentEnvironment))
        {
            resource.AddAttributes(
            [
                new KeyValuePair<string, object>("deployment.environment", option.DeploymentEnvironment)
            ]);
        }
    }

    private static void ConfigureMetrics(MeterProviderBuilder metrics, ModuleOpenTelemetryOption option)
    {
        foreach (var pattern in option.GetSdkMeterPatterns())
        {
            metrics.AddMeter(pattern);
        }

        if (option.IncludeAspNetCoreInstrumentation)
        {
            metrics.AddAspNetCoreInstrumentation();
        }

        if (option.IncludeHttpClientInstrumentation)
        {
            metrics.AddHttpClientInstrumentation();
        }

        if (option.IncludeRuntimeInstrumentation)
        {
            metrics.AddRuntimeInstrumentation();
        }

        if (option.UseOtlpExporter && !option.UseOtlpEnvironmentDefaults)
        {
            metrics.AddOtlpExporter(exporter => ConfigureOtlp(exporter, option));
        }

        if (option.UsePrometheusEndpoint)
        {
            metrics.AddPrometheusExporter();
        }

        if (option.UseConsoleExporter)
        {
            metrics.AddConsoleExporter();
        }
    }

    private static void ConfigureTracing(TracerProviderBuilder tracing, ModuleOpenTelemetryOption option)
    {
        var sources = option.ActivitySourceNames.Where(static source => !string.IsNullOrWhiteSpace(source))
            .Select(static source => source.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length > 0)
            tracing.AddSource(sources);
        if (option.IncludeAspNetCoreInstrumentation)
            tracing.AddAspNetCoreInstrumentation();
        if (option.IncludeHttpClientInstrumentation)
            tracing.AddHttpClientInstrumentation();
        if (option.UseOtlpExporter && !option.UseOtlpEnvironmentDefaults)
            tracing.AddOtlpExporter(exporter => ConfigureOtlp(exporter, option));
        if (option.UseConsoleExporter)
            tracing.AddConsoleExporter();
    }

    private static void ConfigureOtlp(OtlpExporterOptions exporter, ModuleOpenTelemetryOption option)
    {
        if (!string.IsNullOrWhiteSpace(option.OtlpEndpoint))
            exporter.Endpoint = new Uri(option.OtlpEndpoint, UriKind.Absolute);
        exporter.Protocol = option.OtlpProtocol;
    }
}

/// <summary>
/// Registration extensions for the Monica OpenTelemetry integration module.
/// </summary>
public static class ModuleOpenTelemetryRegistrationExtensions
{
    /// <summary>
    /// Enables the SDK's cross-signal OTLP exporter and its endpoint, protocol, header, and signal-specific
    /// environment defaults. Also enables tracing and logging. Cannot be combined with UseOtlpExporter
    /// or separately registered SDK OTLP exporters for this host.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseOtlpExporterFromEnvironment(
        this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module) => module.Configure(options =>
        {
            if (options.UseOtlpExporter && !options.UseOtlpEnvironmentDefaults)
                throw new InvalidOperationException("Select either explicit OTLP options or SDK environment defaults, not both.");
            options.UseOtlpExporter = true;
            options.UseOtlpEnvironmentDefaults = true;
            options.EnableTracing = true;
            options.EnableLogging = true;
        });

    /// <summary>
    /// Enables host-owned tracing with the selected ASP.NET Core and HttpClient instrumentations.
    /// Additional source names are cumulative; no custom application source is inferred. Does not enable an exporter.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="activitySourceNames">Optional application ActivitySource names or SDK-supported wildcard patterns.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseTracing(
        this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module,
        params string[] activitySourceNames)
    {
        ArgumentNullException.ThrowIfNull(activitySourceNames);
        return module.Configure(options =>
        {
            options.EnableTracing = true;
            options.ActivitySourceNames.AddRange(activitySourceNames);
        });
    }

    /// <summary>
    /// Adds the host-owned OpenTelemetry logging provider with formatted messages and scopes by default.
    /// Does not clear other providers or enable an exporter. When Monica.Logging owns the logger factory,
    /// enable its ForwardToProviders option so filtered Serilog events reach this provider.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="includeFormattedMessages">Whether to retain formatted log messages.</param>
    /// <param name="includeScopes">Whether to retain logging scopes.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseLogging(
        this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module,
        bool includeFormattedMessages = true, bool includeScopes = true) => module.Configure(options =>
        {
            options.EnableLogging = true;
            options.IncludeFormattedLogMessages = includeFormattedMessages;
            options.IncludeLogScopes = includeScopes;
        });

    /// <summary>
    /// Enables one OTLP exporter per enabled signal: metrics, plus explicitly enabled tracing and logging.
    /// Endpoint environment variables are honored by the OpenTelemetry SDK when no endpoint is supplied.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="endpoint">Optional absolute OTLP endpoint URI.</param>
    /// <param name="protocol">OTLP transport protocol.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseOtlpExporter(this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module, string? endpoint = null, OtlpExportProtocol protocol = OtlpExportProtocol.Grpc)
    {
        module.Configure(options =>
        {
            if (options.UseOtlpEnvironmentDefaults)
                throw new InvalidOperationException("Select either explicit OTLP options or SDK environment defaults, not both.");
            options.UseOtlpExporter = true;
            options.UseOtlpEnvironmentDefaults = false;
            options.OtlpEndpoint = endpoint;
            options.OtlpProtocol = protocol;
        });
        return module;
    }

    /// <summary>
    /// Exposes a Prometheus scraping endpoint for metrics.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="path">Endpoint path used by Prometheus scrapers. Defaults to <c>/metrics</c>.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UsePrometheusEndpoint(this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module, string path = "/metrics")
    {
        module.Configure(options =>
        {
            options.UsePrometheusEndpoint = true;
            options.PrometheusEndpointPath = path;
        });
        return module;
    }

    /// <summary>
    /// Adds one console exporter per enabled signal for local debugging.
    /// </summary>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseConsoleExporter(this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module)
    {
        module.Configure(options => options.UseConsoleExporter = true);
        return module;
    }

    /// <summary>
    /// Enables Monica's bounded in-process metrics collector used by the snapshot endpoint and dashboard UI.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="configure">Optional additional collector configuration.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseInProcessCollector(this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module, Action<ModuleOpenTelemetryOption>? configure = null)
    {
        module.Configure(options =>
        {
            options.EnableInProcessCollector = true;
            configure?.Invoke(options);
        });
        return module;
    }

    /// <summary>
    /// Controls whether Monica's in-process collector subscribes to meters implied by enabled built-in SDK instrumentations.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="enabled">
    /// <see langword="true"/> to include the ASP.NET Core, HttpClient, and runtime meters selected by the instrumentation flags;
    /// <see langword="false"/> to collect only <see cref="ModuleOpenTelemetryOption.MeterPatterns"/>.
    /// </param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> UseInstrumentationMetersInProcessCollector(this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module, bool enabled = true)
    {
        module.Configure(options => options.IncludeInstrumentationMetersInProcessCollector = enabled);
        return module;
    }

    /// <summary>
    /// Adds another meter name or wildcard pattern to the OpenTelemetry subscription list.
    /// </summary>
    /// <param name="module">The OpenTelemetry registration being configured.</param>
    /// <param name="pattern">Meter name or wildcard pattern such as <c>System.*</c>.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> AddMeter(this ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption> module, string pattern)
    {
        module.Configure(options =>
        {
            if (!options.MeterPatterns.Contains(pattern, StringComparer.Ordinal))
            {
                options.MeterPatterns.Add(pattern);
            }
        });
        return module;
    }

}

/// <summary>
/// Configuration options for Monica's OpenTelemetry SDK wiring.
/// </summary>
public class ModuleOpenTelemetryOption : MinimalApiModuleOptions<ModuleOpenTelemetry>
{
    private static readonly string[] AspNetCoreInstrumentationMeterPatterns = ["Microsoft.AspNetCore.*"];
    private static readonly string[] HttpClientInstrumentationMeterPatterns = ["System.Net.Http", "System.Net.NameResolution"];
    private static readonly string[] RuntimeInstrumentationMeterPatterns = ["System.Runtime", "OpenTelemetry.Instrumentation.Runtime"];

    /// <summary>
    /// Gets or sets the OpenTelemetry resource service name.
    /// When not configured, the module uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>
    /// and resolves to the project name.
    /// </summary>
    public string? ResourceServiceName { get; set; }

    /// <summary>
    /// Gets or sets the optional OpenTelemetry resource service version.
    /// When not configured, the module uses the application version configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// </summary>
    public string? ResourceServiceVersion { get; set; }

    /// <summary>
    /// Resolves the OpenTelemetry resource service name.
    /// </summary>
    public string GetResourceServiceName() => Application.ResolveProjectName(ResourceServiceName);

    /// <summary>
    /// Resolves the OpenTelemetry resource service version.
    /// </summary>
    public string? GetResourceServiceVersion() => Application.ResolveAppVersion(ResourceServiceVersion);

    /// <summary>
    /// Gets or sets the optional deployment environment resource attribute, such as <c>Development</c> or <c>Production</c>.
    /// </summary>
    /// <remarks>When omitted, defaults to Deployment:Scope.</remarks>
    public string? DeploymentEnvironment { get; set; }

    /// <summary>Whether to register a tracing provider. Defaults to false; metrics remain enabled independently.</summary>
    public bool EnableTracing { get; set; }

    /// <summary>Whether to register the OpenTelemetry logging provider. Defaults to false; does not replace the host logger factory.</summary>
    public bool EnableLogging { get; set; }

    /// <summary>Additional ActivitySource names or SDK-supported patterns observed when tracing is enabled. Defaults to empty.</summary>
    public List<string> ActivitySourceNames { get; set; } = [];

    /// <summary>Whether OpenTelemetry log records include formatted messages. Defaults to true; only applies when logging is enabled.</summary>
    public bool IncludeFormattedLogMessages { get; set; } = true;

    /// <summary>Whether OpenTelemetry log records include scopes. Defaults to true; only applies when logging is enabled.</summary>
    public bool IncludeLogScopes { get; set; } = true;

    /// <summary>
    /// Gets custom meter names or wildcard patterns subscribed by the OpenTelemetry SDK and Monica's in-process collector.
    /// Defaults to <c>Monica.*</c>, which captures every first-party Monica meter. Built-in instrumentation meters are
    /// added to the in-process collector separately when <see cref="IncludeInstrumentationMetersInProcessCollector"/> is enabled.
    /// </summary>
    public List<string> MeterPatterns { get; set; } = ["Monica.*"];

    /// <summary>
    /// Gets or sets whether ASP.NET Core metrics and, when enabled, tracing instrumentation is selected. Defaults to true.
    /// </summary>
    public bool IncludeAspNetCoreInstrumentation { get; set; } = true;

    /// <summary>
    /// Gets or sets whether outbound HttpClient metrics and, when enabled, tracing instrumentation is selected. Defaults to true.
    /// </summary>
    public bool IncludeHttpClientInstrumentation { get; set; } = true;

    /// <summary>
    /// Gets or sets whether runtime metrics instrumentation is enabled. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IncludeRuntimeInstrumentation { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the in-process collector should also subscribe to the built-in meters implied by enabled SDK
    /// instrumentation flags. Defaults to <see langword="true"/>, so the dashboard shows ASP.NET Core, HttpClient,
    /// and runtime metrics when those instrumentations are enabled. Set to <see langword="false"/> when the dashboard
    /// should only display meters listed in <see cref="MeterPatterns"/>.
    /// </summary>
    public bool IncludeInstrumentationMetersInProcessCollector { get; set; } = true;

    /// <summary>
    /// Gets or sets whether OTLP export is enabled for metrics and any explicitly enabled tracing or logging.
    /// The OpenTelemetry SDK honors OTLP endpoint environment variables unless OtlpEndpoint overrides them.
    /// Protocol is explicitly selected by OtlpProtocol. Defaults to false.
    /// </summary>
    public bool UseOtlpExporter { get; set; }

    internal bool UseOtlpEnvironmentDefaults { get; set; }

    /// <summary>
    /// Gets or sets an optional absolute OTLP endpoint URI that overrides SDK environment configuration.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the OTLP transport protocol. Defaults to gRPC.
    /// </summary>
    public OtlpExportProtocol OtlpProtocol { get; set; } = OtlpExportProtocol.Grpc;

    /// <summary>
    /// Gets or sets whether to expose a Prometheus scraping endpoint. Defaults to <see langword="false"/>.
    /// </summary>
    public bool UsePrometheusEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the Prometheus scraping endpoint path. Defaults to <c>/metrics</c>.
    /// </summary>
    public string PrometheusEndpointPath { get; set; } = "/metrics";

    /// <summary>
    /// Gets or sets whether to enable console export for metrics and any explicitly enabled tracing or logging.
    /// </summary>
    public bool UseConsoleExporter { get; set; }

    /// <summary>
    /// Gets or sets whether to enable the bounded in-process collector used by snapshot APIs and the dashboard UI.
    /// </summary>
    public bool EnableInProcessCollector { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of samples retained for each instrument tag set.
    /// Defaults to 600. Values below one retain one sample. Higher values increase per-process memory usage.
    /// </summary>
    public int SamplesPerSeries { get; set; } = 600;

    /// <summary>
    /// Gets or sets the maximum number of distinct tag sets retained per instrument.
    /// Defaults to 200. Non-positive values admit no tag sets. When exceeded, the collector reports overflow
    /// and drops observations for additional tag sets while continuing to record already retained series.
    /// </summary>
    public int MaxTagSetsPerInstrument { get; set; } = 200;

    /// <summary>
    /// Gets or sets how often observable instruments are sampled by the in-process collector.
    /// </summary>
    public TimeSpan ObservableInstrumentInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the normalized custom meter patterns supplied to the OpenTelemetry SDK through <c>AddMeter</c>.
    /// Built-in instrumentation meters are activated through their dedicated SDK instrumentation calls.
    /// </summary>
    /// <returns>The distinct, non-empty SDK meter patterns in configured order.</returns>
    public IReadOnlyList<string> GetSdkMeterPatterns()
    {
        return NormalizeMeterPatterns(MeterPatterns);
    }

    /// <summary>
    /// Gets the normalized meter patterns used by Monica's in-process collector.
    /// </summary>
    /// <returns>
    /// The custom meter patterns plus built-in instrumentation meters selected by the enabled instrumentation flags.
    /// An empty list intentionally means the collector accepts every published meter.
    /// </returns>
    public IReadOnlyList<string> GetInProcessCollectorMeterPatterns()
    {
        var patterns = NormalizeMeterPatterns(MeterPatterns);
        if (patterns.Count == 0 || !IncludeInstrumentationMetersInProcessCollector)
        {
            return patterns;
        }

        if (IncludeAspNetCoreInstrumentation)
        {
            AddMeterPatterns(patterns, AspNetCoreInstrumentationMeterPatterns);
        }

        if (IncludeHttpClientInstrumentation)
        {
            AddMeterPatterns(patterns, HttpClientInstrumentationMeterPatterns);
        }

        if (IncludeRuntimeInstrumentation)
        {
            AddMeterPatterns(patterns, RuntimeInstrumentationMeterPatterns);
        }

        return patterns;
    }

    private static List<string> NormalizeMeterPatterns(IEnumerable<string> patterns)
    {
        return patterns
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => pattern.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void AddMeterPatterns(List<string> target, IReadOnlyList<string> source)
    {
        foreach (var pattern in source)
        {
            if (!target.Contains(pattern, StringComparer.Ordinal))
            {
                target.Add(pattern);
            }
        }
    }
}
