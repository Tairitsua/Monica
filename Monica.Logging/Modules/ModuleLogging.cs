using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Extensions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Logging.Providers.Serilog;
using Monica.Logging.Services;
using Monica.Tool.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleLoggingBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure Logging module
        /// </summary>
        public ModuleRegistration<ModuleLogging, ModuleLoggingOption> AddLogging(
            Action<ModuleLoggingOption>? action = null)
        {
            return builder.AddModule<ModuleLogging, ModuleLoggingOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleLogging, ModuleLoggingOption> registration)
    {
        public ModuleRegistration<ModuleLogging, ModuleLoggingOption> AddRequestResponseLoggingMiddleware(
            bool disableResponse = false,
            bool disableRequest = false)
        {
            return registration
                .RequireWebHost("Request/response logging middleware must run in an ASP.NET Core request pipeline.")
                .ConfigureServices(context =>
                {
                    context.Services.AddTransient<HttpRequestLoggingMiddleware>();
                    context.Services.AddTransient<HttpResponseLoggingMiddleware>();
                })
                .ConfigureApplicationBuilder(context =>
                {
                    if (!disableRequest)
                    {
                        context.ApplicationBuilder.UseMiddleware<HttpRequestLoggingMiddleware>();
                    }

                    if (!disableResponse)
                    {
                        context.ApplicationBuilder.UseMiddleware<HttpResponseLoggingMiddleware>();
                    }
                }, ModuleWebStage.BeforeRouting);
        }
    }
}

public class ModuleLogging : MonicaModule<ModuleLoggingOption>, IWebModule
{
    public override void ConfigureBuilder(ModuleBuilderContext<ModuleLoggingOption> context)
    {
        var builder = context.HostApplicationBuilder;
        var providers = Option.ForwardToProviders ? new LoggerProviderCollection() : null;
        var serilogLogger = SerilogLoggingBootstrapper.CreateLogger(builder.Configuration, Option, providers);

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(serilogLogger, dispose: true, providers: providers);

        UseCompositionLoggerFactory(new SerilogLoggerFactory(serilogLogger, dispose: false));

        var level =
            builder.Configuration.GetSectionRecursively("Serilog:MinimumLevel").Select(p => new { p.Key, p.Value }).ToList().ToJsonString();
        Logger.LogInformation("Set logging level: {LoggingLevel}", level);
    }
}

public class ModuleLoggingOption : ModuleOptions<ModuleLogging>
{
    /// <summary>
    /// Replaces the default Monica Serilog setup with a custom logger factory.
    /// </summary>
    public Func<LoggerConfiguration, Logger>? CustomLoggerFactory { get; set; }

    /// <summary>
    /// Adds filters, enrichers, or sinks after Monica's default logger configuration, before the host-owned
    /// logger is created. Defaults to <see langword="null"/>. Do not create or dispose a logger in this callback.
    /// This callback is not used when <see cref="CustomLoggerFactory"/> replaces the default configuration.
    /// </summary>
    public Action<LoggerConfiguration>? ConfigureLogger { get; set; }

    /// <summary>
    /// Forwards filtered Serilog events to other host-registered <see cref="ILoggerProvider"/> instances,
    /// such as OpenTelemetry. Defaults to <see langword="false"/>. Register those providers after this module's
    /// builder phase; the host owns their lifetime. Avoid adding a second sink for the same destination.
    /// </summary>
    public bool ForwardToProviders { get; set; }

    /// <summary>
    /// Gets paths whose ASP.NET Core hosting diagnostics containing a non-empty query string are excluded.
    /// Defaults to empty. Paths match exactly or below a slash-delimited prefix, ignoring case and trailing
    /// slashes. The entire hosting event is excluded; ordinary request logs without a query remain available.
    /// Use explicit paths for transports that carry credentials in queries; no parameter name is assumed.
    /// </summary>
    public List<string> SensitiveQueryStringPathPrefixes { get; set; } = [];

    /// <summary>
    /// <a href="https://github.com/serilog/serilog/wiki/Formatting-Output">Serilog output template documentation</a>
    /// </summary>
    public string OutputTemplate { get; set; } = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {ThreadName}TID{ThreadId}] {Message:lj}{Exception}{NewLine}";

    public bool EnableConsoleSink { get; set; } = true;
    public bool EnableFileSink { get; set; } = true;

    /// <summary>
    /// Sets the explicit log file path including file name. When specified, <see cref="LogDirectory"/> and
    /// <see cref="LogFileName"/> are ignored.
    /// </summary>
    public string? LogFilePath { get; set; }

    /// <summary>Base directory for the default file sink. Logs is appended before LogFileName; defaults to AppContext.BaseDirectory.</summary>
    public string? LogDirectory { get; set; }
    /// <summary>File name beneath the Logs child directory; defaults to the entry assembly name with a .log extension.</summary>
    public string LogFileName { get; set; } = $"{Assembly.GetEntryAssembly()!.GetName().Name}.log";

    /// <summary>Maximum file size before writes stop or roll. Defaults to one GiB; null removes the size limit.</summary>
    public long? FileSizeLimitBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Whether reaching the size limit starts another file. Defaults to false.</summary>
    public bool RollOnFileSizeLimit { get; set; }

    /// <summary>Time-based file rolling interval. Defaults to Infinite, which disables time-based rolling.</summary>
    public RollingInterval RollingInterval { get; set; } = RollingInterval.Infinite;

    /// <summary>Maximum retained log-file count, including the current file. Defaults to 31; null removes the count limit.</summary>
    public int? RetainedFileCountLimit { get; set; } = 31;

    /// <summary>Optional age limit for retained files. Defaults to null. The file sink enforces retention when opening or rolling files.</summary>
    public TimeSpan? RetainedFileTimeLimit { get; set; }

    public bool EnableThreadNameEnricher { get; set; }
    public bool EnableThreadIdEnricher { get; set; } = true;
    public bool EnableTraceIdEnricher { get; set; }
}
