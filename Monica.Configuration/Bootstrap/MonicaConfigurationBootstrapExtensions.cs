using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Bootstrap;

/// <summary>
/// Provides bootstrap-time helpers for reading Monica <see cref="ConfigurationAttribute"/> options before
/// the Monica effective-value store has been activated.
/// </summary>
public static class MonicaConfigurationBootstrapExtensions
{
    /// <summary>
    /// Binds a Monica configuration options type from the host bootstrap <see cref="IConfiguration"/>.
    /// </summary>
    /// <typeparam name="TOptions">The options type marked with <see cref="ConfigurationAttribute"/>.</typeparam>
    /// <param name="configuration">The host configuration available during application builder setup.</param>
    /// <param name="logger">The host-owned logger used for bootstrap diagnostics.</param>
    /// <param name="debugging">Whether to log provider and file-source diagnostics for the resolved section.</param>
    /// <returns>
    /// The bound options object, or a CLR default instance when the section is unavailable or cannot be bound.
    /// </returns>
    /// <remarks>
    /// This helper is intended for bootstrap settings such as database connectivity that must be available before
    /// Monica-managed configuration is loaded. Runtime-managed settings should still flow through Monica.Configuration.
    /// Bootstrap binding honors an explicit <see cref="ConfigurationAttribute.SectionPath"/>. When the attribute omits
    /// the path, bootstrap uses the short CLR type name because module options are not available during bootstrap.
    /// </remarks>
    public static TOptions GetMonicaBootstrapConfiguration<TOptions>(
        this IConfiguration configuration,
        ILogger logger,
        bool debugging = false)
        where TOptions : class, new()
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var optionsType = typeof(TOptions);
        var sectionPath = ResolveSectionPath(optionsType, logger);
        if (sectionPath is null)
        {
            return new TOptions();
        }

        if (debugging)
        {
            LogProviderDiagnostics(configuration, optionsType, sectionPath, logger);
        }

        return BindConfiguration<TOptions>(configuration, sectionPath, logger);
    }

    private static string? ResolveSectionPath(Type optionsType, ILogger logger)
    {
        var attribute = optionsType.GetCustomAttribute<ConfigurationAttribute>(inherit: false);
        if (attribute is null)
        {
            logger.LogWarning(
                "Type '{OptionsType}' is not marked with {AttributeType}. Monica bootstrap binding returned CLR defaults.",
                optionsType.FullName ?? optionsType.Name,
                nameof(ConfigurationAttribute));

            return null;
        }

        return ConfigurationSectionPathResolver.Resolve(
            optionsType,
            attribute,
            ConfigurationSectionPathConvention.ShortTypeName);
    }

    private static TOptions BindConfiguration<TOptions>(
        IConfiguration configuration,
        string sectionPath,
        ILogger logger)
        where TOptions : class, new()
    {
        var section = configuration.GetSection(sectionPath);
        if (!section.Exists())
        {
            logger.LogWarning(
                "Configuration section '{SectionPath}' for Monica bootstrap options '{OptionsType}' was not found. CLR defaults are used.",
                sectionPath,
                typeof(TOptions).FullName ?? typeof(TOptions).Name);

            return new TOptions();
        }

        try
        {
            return MonicaConfigurationBinder.Get<TOptions>(configuration, sectionPath);
        }
        catch (Exception ex)
        {
            var safeFault = ex is ConfigurationValidationExecutionException typed
                ? new ConfigurationValidationExecutionException(typeof(TOptions).FullName ?? typeof(TOptions).Name,
                    typed.LogicalPath, typed.Stage, typed.Kind)
                : new ConfigurationValidationExecutionException(typeof(TOptions).FullName ?? typeof(TOptions).Name,
                    LogicalPath.Root, "bootstrap-binding");
            logger.LogWarning(
                safeFault,
                "Failed to bind configuration section '{SectionPath}' to Monica bootstrap options '{OptionsType}'. CLR defaults are used.",
                sectionPath,
                typeof(TOptions).FullName ?? typeof(TOptions).Name);
        }

        return new TOptions();
    }

    private static void LogProviderDiagnostics(
        IConfiguration configuration,
        Type optionsType,
        string sectionPath,
        ILogger logger)
    {
        logger.LogInformation(
            "Reading Monica bootstrap options '{OptionsType}' from section '{SectionPath}'.",
            optionsType.FullName ?? optionsType.Name,
            sectionPath);

        if (configuration is not IConfigurationRoot root)
        {
            logger.LogInformation(
                "Configuration root does not expose providers, so provider diagnostics for section '{SectionPath}' are unavailable.",
                sectionPath);
            return;
        }

        var contributors = root.Providers
            .Select((provider, index) => new ProviderDiagnostic(provider, index))
            .Where(diagnostic => ProviderContributesSection(diagnostic.Provider, sectionPath))
            .OrderByDescending(diagnostic => diagnostic.PriorityIndex)
            .ToArray();

        if (contributors.Length == 0)
        {
            logger.LogInformation(
                "No configuration provider contributes section '{SectionPath}'. Registered provider count: {ProviderCount}.",
                sectionPath,
                root.Providers.Count());
            return;
        }

        var highestPriorityIndex = contributors.Max(diagnostic => diagnostic.PriorityIndex);
        foreach (var contributor in contributors)
        {
            var source = DescribeSource(contributor.Provider);
            logger.LogInformation(
                "Bootstrap section '{SectionPath}' contributor #{ProviderIndex}: {ProviderType}. HighestPriority={HighestPriority}. SourcePath={SourcePath}. PhysicalPath={PhysicalPath}.",
                sectionPath,
                contributor.PriorityIndex,
                contributor.Provider.GetType().FullName ?? contributor.Provider.GetType().Name,
                contributor.PriorityIndex == highestPriorityIndex,
                source.SourcePath ?? string.Empty,
                source.PhysicalPath ?? string.Empty);
        }
    }

    private static bool ProviderContributesSection(IConfigurationProvider provider, string sectionPath)
    {
        return provider.TryGet(sectionPath, out _)
               || provider.GetChildKeys(Array.Empty<string>(), sectionPath).Any();
    }

    private static ProviderSourceDiagnostic DescribeSource(IConfigurationProvider provider)
    {
        if (provider is not JsonConfigurationProvider jsonProvider)
        {
            return new ProviderSourceDiagnostic(null, null);
        }

        var sourcePath = jsonProvider.Source.Path;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new ProviderSourceDiagnostic(sourcePath, null);
        }

        var physicalPath = jsonProvider.Source.FileProvider?.GetFileInfo(sourcePath)?.PhysicalPath;
        return new ProviderSourceDiagnostic(sourcePath, NormalizePhysicalPath(physicalPath));
    }

    private static string? NormalizePhysicalPath(string? physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(physicalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return physicalPath;
        }
    }

    private sealed record ProviderDiagnostic(IConfigurationProvider Provider, int PriorityIndex);

    private sealed record ProviderSourceDiagnostic(string? SourcePath, string? PhysicalPath);
}
