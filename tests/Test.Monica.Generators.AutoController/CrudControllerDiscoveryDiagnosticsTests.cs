using System.Collections.Concurrent;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Services.Support;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class CrudControllerDiscoveryDiagnosticsTests
{
    [Fact]
    public void FeatureProvider_ShouldSelectCrudMarkerWithoutRuntimeOptionOrLoggerDependencies()
    {
        var provider = new TestFeatureProvider();

        provider.IsControllerType(typeof(MismatchedCrudService)).Should().BeTrue();
        provider.IsControllerType(typeof(OrdinaryService)).Should().BeFalse();
    }

    [Fact]
    public void DiscoveryDiagnostic_ShouldDescribeOnlyPrefixMismatches()
    {
        var mismatch = CrudControllerPrefixMismatch.Create(
            typeof(MismatchedCrudService),
            "ApplicationService");

        mismatch.Should().NotBeNull();
        mismatch!.ControllerName.Should().Be(nameof(MismatchedCrudService));
        mismatch.RequiredPrefix.Should().Be("ApplicationService");
        CrudControllerPrefixMismatch.Create(
                typeof(ApplicationServiceMatchingCrud),
                "ApplicationService")
            .Should().BeNull();
        CrudControllerPrefixMismatch.Create(typeof(OrdinaryService), "Service")
            .Should().BeNull();
    }

    [Fact]
    public void ApplicationPartCatalog_ShouldAcceptEachDiscoveredTypeOnce()
    {
        var catalog = new AutoControllerApplicationPartCatalog();

        catalog.Add(typeof(MismatchedCrudService)).Should().BeTrue();
        catalog.Add(typeof(MismatchedCrudService)).Should().BeFalse();
        catalog.GetApplicationPartTypes().Should().ContainSingle()
            .Which.Should().Be(typeof(MismatchedCrudService));
    }

    [Fact]
    public void ModuleDiscovery_WhenPrefixDoesNotMatch_ShouldLogOnceThroughCompositionLogger()
    {
        var loggerFactory = new RecordingLoggerFactory();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(CrudControllerDiscoveryDiagnosticsTests).Assembly));
            monica.AddModule<CompositionLoggerProbeModule, CompositionLoggerProbeModuleOption>(options =>
                options.LoggerFactory = loggerFactory);
            monica.AddAutoControllers(
                configureCrud: options => options.ServiceNamePrefix = "ApplicationService");
        });
        using var application = builder.Build();

        var mismatchLogs = loggerFactory.Entries
            .Where(entry =>
                entry.Level == LogLevel.Warning
                && entry.Properties.GetValueOrDefault("ControllerName")?.ToString()
                == nameof(MismatchedCrudService))
            .ToArray();

        mismatchLogs.Should().ContainSingle();
        mismatchLogs[0].Properties.GetValueOrDefault("DiagnosticSource")
            .Should().Be(CrudControllerPrefixMismatch.SOURCE);
        mismatchLogs[0].Properties.GetValueOrDefault("RequiredPrefix")
            .Should().Be("ApplicationService");
    }

    private sealed class TestFeatureProvider : CrudControllerFeatureProvider
    {
        internal bool IsControllerType(Type type)
        {
            return IsController(type.GetTypeInfo());
        }
    }

    private sealed class MismatchedCrudService : ICrudApplicationService;

    private sealed class ApplicationServiceMatchingCrud : ICrudApplicationService;

    private sealed class OrdinaryService;

    public sealed class CompositionLoggerProbeModule
        : MonicaModule<CompositionLoggerProbeModuleOption>
    {
        public override void ValidateOptions(
            CompositionLoggerProbeModuleOption options,
            string? profileName)
        {
            UseCompositionLoggerFactory(options.LoggerFactory);
        }
    }

    public sealed class CompositionLoggerProbeModuleOption
        : ModuleOptions<CompositionLoggerProbeModule>
    {
        public ILoggerFactory LoggerFactory { get; set; } = NullLoggerFactory.Instance;
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<LogEntry> _entries = [];

        internal IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new RecordingLogger(categoryName, _entries);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(
        string categoryName,
        ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(
                    static value => value.Key,
                    static value => value.Value,
                    StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            entries.Enqueue(new LogEntry(
                categoryName,
                logLevel,
                formatter(state, exception),
                properties));
        }
    }

    private sealed record LogEntry(
        string CategoryName,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);
}
