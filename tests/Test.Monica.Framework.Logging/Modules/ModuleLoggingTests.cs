using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Test.Monica.Framework.Logging.Modules;

public sealed class ModuleLoggingTests
{
    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "/signalr", "?access%5Ftoken=secret", true)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "/SIGNALR/negotiate", "?x=secret", true)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "/signalr-other", "?x=value", false)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "/signalr", "?", false)]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", "/api/configuration", "?category=SYS", false)]
    [InlineData("Other.Diagnostics", "/signalr", "?x=value", false)]
    public async Task Logging_WhenSensitivePathsAreConfigured_ShouldExcludeOnlyMatchingHostingQueries(
        string category, string path, string query, bool excluded)
    {
        var sink = new RecordingSink();
        var factory = new LoggingFactory(options =>
        {
            options.EnableFileSink = false;
            options.SensitiveQueryStringPathPrefixes = [" /signalr/ "];
            options.ConfigureLogger = logger => logger.WriteTo.Sink(sink);
        });
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);

        logger.LogWarning("Request {RequestPath} {QueryString} {Marker}", path, query, "filter-case");

        sink.Events.Any(item => item.Properties.ContainsKey("Marker")).Should().Be(!excluded);
    }

    [Fact]
    public async Task Logging_WhenCustomized_ShouldRetainTheNativeFileSinkAndApplyTheHook()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "native.log");
            var sink = new RecordingSink();
            var factory = new LoggingFactory(options =>
            {
                options.LogFilePath = path;
                options.ConfigureLogger = logger => logger.Enrich.WithProperty("Scenario", "customized")
                    .WriteTo.Sink(sink);
            });
            await using (var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
            {
                app.Services.GetRequiredService<ILogger<ModuleLoggingTests>>().LogWarning("native-hook-marker");
            }

            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Contain("native-hook-marker");
            sink.Events.Should().Contain(item => item.RenderMessage() == "native-hook-marker"
                && item.Properties["Scenario"].ToString() == "\"customized\"");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Logging_WhenSizeRollingIsEnabled_ShouldKeepOnlyTheConfiguredNumberOfFiles()
    {
        var directory = CreateDirectory();
        try
        {
            var factory = new LoggingFactory(options =>
            {
                options.LogFilePath = Path.Combine(directory, "rolling.log");
                options.OutputTemplate = "{Message:lj}{NewLine}";
                options.FileSizeLimitBytes = 256;
                options.RollOnFileSizeLimit = true;
                options.RetainedFileCountLimit = 2;
                options.ConfigureLogger = logger => logger.MinimumLevel.Warning();
            });
            await using (var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
            {
                var logger = app.Services.GetRequiredService<ILogger<ModuleLoggingTests>>();
                for (var index = 0; index < 20; index++)
                    logger.LogWarning("rolling-{Index:D2} {Padding}", index, new string('x', 220));
            }

            var files = Directory.GetFiles(directory, "rolling*.log");
            files.Should().HaveCount(2);
            var contents = string.Join("\n", files.Select(File.ReadAllText));
            contents.Should().Contain("rolling-19").And.NotContain("rolling-00");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "monica-logging-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed class LoggingFactory(Action<ModuleLoggingOption> configure)
        : MonicaTestApplicationFactory<LoggingFactory>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            builder.AddLogging(options =>
            {
                options.EnableConsoleSink = false;
                configure(options);
            });
        }
    }
}
