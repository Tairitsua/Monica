using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Extensions.Logging;
using Monica.Logging.Extensions;
using Monica.Logging.Services.Support;
using Monica.Modules;

namespace Monica.Logging.Providers.Serilog;

internal static class SerilogLoggingBootstrapper
{
    public static Logger CreateLogger(IConfiguration configuration, ModuleLoggingOption option,
        LoggerProviderCollection? providers = null)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration);

        var sensitivePaths = option.SensitiveQueryStringPathPrefixes
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim().TrimEnd('/'))
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sensitivePaths.Length > 0)
            loggerConfiguration.Filter.ByExcluding(logEvent => HostingRequestQueryFilter.Excludes(logEvent, sensitivePaths));
        if (providers is not null)
            loggerConfiguration.WriteTo.Providers(providers);

        if (option.CustomLoggerFactory is { } customLoggerFactory)
        {
            return customLoggerFactory(loggerConfiguration);
        }

        var configuredLogger = ApplyEnrichers(loggerConfiguration, option);
        configuredLogger = ApplySinks(configuredLogger, option);
        option.ConfigureLogger?.Invoke(configuredLogger);

        return configuredLogger.CreateLogger();
    }

    private static LoggerConfiguration ApplyEnrichers(LoggerConfiguration configuration, ModuleLoggingOption option)
    {
        if (option.EnableThreadIdEnricher)
        {
            configuration = configuration.Enrich.WithThreadId();
        }

        if (option.EnableThreadNameEnricher)
        {
            configuration = configuration.Enrich.WithThreadName();
        }

        if (option.EnableTraceIdEnricher)
        {
            configuration = configuration.Enrich.WithTraceId();
        }

        return configuration;
    }

    private static LoggerConfiguration ApplySinks(LoggerConfiguration configuration, ModuleLoggingOption option)
    {
        if (option.EnableConsoleSink)
        {
            configuration = configuration.WriteTo.Async(sink =>
                sink.Console(outputTemplate: option.OutputTemplate));
        }

        if (option.EnableFileSink)
        {
            configuration = configuration.WriteTo.Async(sink =>
                sink.File(LogFileLocation.Resolve(option), outputTemplate: option.OutputTemplate,
                    fileSizeLimitBytes: option.FileSizeLimitBytes,
                    rollOnFileSizeLimit: option.RollOnFileSizeLimit,
                    rollingInterval: option.RollingInterval,
                    retainedFileCountLimit: option.RetainedFileCountLimit,
                    retainedFileTimeLimit: option.RetainedFileTimeLimit));
        }

        return configuration;
    }

}
