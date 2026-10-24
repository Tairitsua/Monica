using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Monica.Core;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Test.Monica.OpenTelemetry.Modules;

public sealed class ModuleOpenTelemetrySignalTests
{
    [Fact]
    public async Task ManualHost_KeepsLocalTelemetryWithoutOtlpAndDefaultsDeploymentEnvironment()
    {
        await using var app = await new SignalFactory(useEnvironmentOtlp: true, manual: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var options = app.Services.GetRequiredService<IOptions<ModuleOpenTelemetryOption>>().Value;
        Assert.False(options.UseOtlpExporter);
        Assert.Equal("shared-uat", options.DeploymentEnvironment);
        var meter = app.Services.GetRequiredService<MeterProvider>();
        Assert.Contains(meter.GetResource().Attributes,
            item => item.Key == "deployment.environment" && Equals(item.Value, "shared-uat"));
        Assert.Contains(app.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.Key == "OpenTelemetry:otlp-export" && resource.Mode == ActivationMode.Manual);
    }

    [Fact]
    public async Task AddOpenTelemetry_WhenSignalsAreNotSelected_ShouldKeepTheMetricsOnlyDefault()
    {
        var factory = new SignalFactory();
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        app.Services.GetRequiredService<MeterProvider>().Should().NotBeNull();
        app.Services.GetService<TracerProvider>().Should().BeNull();
        app.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>().Should().BeEmpty();
    }

    [Fact]
    public async Task Signals_WhenExplicitlySelected_ShouldExportOnceWithSharedIdentityAndLiveScopes()
    {
        var factory = new SignalFactory(enableSignals: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var source = new ActivitySource(factory.SourceName);
        using (source.StartActivity("test-operation")) { }
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SignalCase");
        using (logger.BeginScope(new Dictionary<string, object> { ["scope-marker"] = "scope-value" }))
            logger.LogWarning("signal {Marker}", "test-log");

        factory.Activities.Names.Should().ContainSingle().Which.Should().Be("test-operation");
        var record = factory.Logs.Records.Should().ContainSingle(item => item.Category == "SignalCase").Which;
        record.Message.Should().Be("signal test-log");
        record.Scopes.Should().Contain("scope-marker=scope-value");
        app.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>().Should().ContainSingle();
        var resource = app.Services.GetRequiredService<TracerProvider>().GetResource();
        resource.Attributes.Should().Contain(item => item.Key == "service.name" && Equals(item.Value, "signal-test-host"));
    }

    [Fact]
    public async Task Logging_WhenSerilogForwardsProviders_ShouldExportOnceAfterSensitiveQueryFiltering()
    {
        var factory = new SignalFactory(enableSignals: true, useSerilog: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");

        logger.LogWarning("request {RequestPath} {QueryString}", "/signalr/negotiate", "?access%5Ftoken=secret");
        logger.LogWarning("request {RequestPath} {QueryString}", "/api/visible", "?page=1");

        var records = factory.Logs.Records.Where(item => item.Category == "Microsoft.AspNetCore.Hosting.Diagnostics").ToArray();
        records.Should().ContainSingle().Which.Message.Should().Contain("/api/visible");
        records.Should().NotContain(item => item.Message.Contains("secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Otlp_WhenBothModesAreSelected_ShouldRejectCompositionBeforeStartingExport(bool environmentFirst)
    {
        var factory = new SignalFactory(configure: registration =>
        {
            if (environmentFirst)
                registration.UseOtlpExporterFromEnvironment().UseOtlpExporter();
            else
                registration.UseOtlpExporter().UseOtlpExporterFromEnvironment();
        });

        var create = async () => await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var error = await create.Should().ThrowAsync<Exception>();
        error.Which.ToString().Should().Contain("Select either explicit OTLP options or SDK environment defaults, not both.");
    }

    [Fact]
    public async Task Otlp_WhenEnvironmentModeIsSelected_ShouldEnableAllSignalsWithoutDuplicateExporters()
    {
        // The SDK exporter is exercised with a scenario-owned HTTP boundary; no collector or network is used.
        var factory = new SignalFactory(useEnvironmentOtlp: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        app.Services.GetRequiredService<MeterProvider>().Should().NotBeNull();
        app.Services.GetRequiredService<TracerProvider>().Should().NotBeNull();
        app.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>().Should().ContainSingle();
        var options = app.Services.GetRequiredService<IOptions<ModuleOpenTelemetryOption>>().Value;
        options.EnableLogging.Should().BeTrue();
        options.EnableTracing.Should().BeTrue();
    }

    private sealed class SignalFactory(bool enableSignals = false, bool useSerilog = false,
        bool useEnvironmentOtlp = false,
        Action<ModuleRegistration<ModuleOpenTelemetry, ModuleOpenTelemetryOption>>? configure = null,
        bool manual = false)
        : MonicaTestApplicationFactory<SignalFactory>
    {
        public string SourceName { get; } = "Monica.SignalTests." + Guid.NewGuid().ToString("N");
        public ActivityExporter Activities { get; } = new();
        public LogExporter Logs { get; } = new();

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            base.ConfigureHost(builder);
            builder.Configuration["Application:AutonomousWork"] = manual ? "false" : "true";
            builder.Configuration["Application:DeploymentScope"] = "shared-uat";
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            if (useSerilog)
                builder.AddLogging(options =>
                {
                    options.EnableFileSink = false;
                    options.EnableConsoleSink = false;
                    options.ForwardToProviders = true;
                    options.SensitiveQueryStringPathPrefixes = ["/signalr"];
                });
            var registration = builder.AddOpenTelemetry(options =>
            {
                options.ResourceServiceName = "signal-test-host";
                options.IncludeAspNetCoreInstrumentation = false;
                options.IncludeHttpClientInstrumentation = false;
                options.IncludeRuntimeInstrumentation = false;
                options.MeterPatterns = [SourceName];
            });
            if (enableSignals)
            {
                registration.UseTracing(SourceName).UseLogging();
                builder.AddOpenTelemetry().UseTracing(SourceName).UseLogging();
            }
            if (useEnvironmentOtlp)
                registration.UseOtlpExporterFromEnvironment();
            configure?.Invoke(registration);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            if (enableSignals)
                services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(Activities)))
                    .WithLogging(logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(Logs)));
            if (useEnvironmentOtlp)
                services.ConfigureAll<OtlpExporterOptions>(options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.HttpClientFactory = () => new HttpClient(new CollectorBoundary());
                });
        }
    }

    private sealed class ActivityExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<string> Names { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
                Names.Enqueue(activity.OperationName);
            return ExportResult.Success;
        }
    }

    private sealed class LogExporter : BaseExporter<LogRecord>
    {
        public ConcurrentQueue<LogSnapshot> Records { get; } = new();
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var scopes = new List<string>();
                record.ForEachScope(static (scope, values) =>
                {
                    foreach (var pair in scope)
                        values.Add(pair.Key + "=" + pair.Value);
                }, scopes);
                Records.Enqueue(new LogSnapshot(record.CategoryName ?? string.Empty,
                    record.FormattedMessage ?? string.Empty, scopes));
            }
            return ExportResult.Success;
        }
    }

    private sealed record LogSnapshot(string Category, string Message, IReadOnlyList<string> Scopes);

    private sealed class CollectorBoundary : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
    }
}
