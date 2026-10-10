using Microsoft.Extensions.Primitives;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

/// <summary>
/// Builds source-aware validation reports for the current runtime configuration.
/// </summary>
internal sealed class ConfigurationRuntimeValidationService(
    IConfigurationDefinitionRegistry definitionRegistry,
    ConfigurationValidationCoordinator validationCoordinator,
    IConfigurationSourceInspector sourceInspector,
    MonicaConfigurationProviderAccessor providerAccessor,
    ConfigurationRuntimeContext runtimeContext)
    : IConfigurationRuntimeValidationService, IDisposable
{
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, ConfigurationValidationReport> _definitionReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimeReloadRevisionTracker _runtimeReloadRevisionTracker = new(runtimeContext);
    private ValidationCacheRevision _cachedRevision = ValidationCacheRevision.Empty;
    private ConfigurationValidationReport? _fullReport;

    /// <summary>
    /// Builds a validation report for every local configuration definition.
    /// </summary>
    /// <returns>The validation report.</returns>
    public ConfigurationValidationReport GetReport()
    {
        lock (_cacheLock)
        {
            while (true)
            {
                var revision = PrepareCache();
                if (_fullReport is not null)
                {
                    return _fullReport;
                }

                var definitionReports = definitionRegistry.GetAll()
                    .Select(GetOrBuildDefinitionReport)
                    .ToArray();
                if (GetCurrentRevision() != revision)
                {
                    ResetCache(GetCurrentRevision());
                    continue;
                }

                _fullReport = new ConfigurationValidationReport
                {
                    Coverage = definitionReports.All(report => report.Coverage == ConfigurationValidationCoverage.Complete)
                        ? ConfigurationValidationCoverage.Complete : definitionReports.Any(report => report.Coverage == ConfigurationValidationCoverage.Failed)
                            ? ConfigurationValidationCoverage.Failed : ConfigurationValidationCoverage.SchemaOnly,
                    Issues = Array.AsReadOnly(definitionReports.SelectMany(report => report.Issues).ToArray()),
                    DefinitionReports = Array.AsReadOnly(definitionReports.SelectMany(report => report.DefinitionReports).ToArray())
                };
                return _fullReport;
            }
        }
    }

    /// <summary>
    /// Builds a validation report for one local configuration definition.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <returns>The validation report.</returns>
    public ConfigurationValidationReport GetReport(string definitionKey)
    {
        var definition = definitionRegistry.GetRequired(definitionKey);
        lock (_cacheLock)
        {
            while (true)
            {
                var revision = PrepareCache();
                var report = GetOrBuildDefinitionReport(definition);
                if (GetCurrentRevision() == revision)
                {
                    return report;
                }

                ResetCache(GetCurrentRevision());
            }
        }
    }

    private ValidationCacheRevision PrepareCache()
    {
        var revision = GetCurrentRevision();
        if (_cachedRevision != revision)
        {
            ResetCache(revision);
        }

        return revision;
    }

    private ValidationCacheRevision GetCurrentRevision()
    {
        return new ValidationCacheRevision(
            providerAccessor.SuccessfulProjectionRevision,
            _runtimeReloadRevisionTracker.Revision);
    }

    private void ResetCache(ValidationCacheRevision revision)
    {
        _cachedRevision = revision;
        _definitionReports.Clear();
        _fullReport = null;
    }

    private ConfigurationValidationReport GetOrBuildDefinitionReport(ConfigurationDefinition definition)
    {
        if (_definitionReports.TryGetValue(definition.DefinitionKey, out var report))
        {
            return report;
        }

        report = ConfigurationValidationReportFactory.Create(definition,
            validationCoordinator.ValidateConfiguration(definition, runtimeContext.Configuration), sourceInspector);
        _definitionReports[definition.DefinitionKey] = report;
        return report;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runtimeReloadRevisionTracker.Dispose();
    }

    private readonly record struct ValidationCacheRevision(long ProjectionRevision, long RuntimeReloadRevision)
    {
        internal static ValidationCacheRevision Empty { get; } = new(-1, -1);
    }

    private sealed class RuntimeReloadRevisionTracker : IDisposable
    {
        private readonly IDisposable? _registration;
        private long _revision;

        internal RuntimeReloadRevisionTracker(ConfigurationRuntimeContext runtimeContext)
        {
            _registration = runtimeContext.Root is { } root
                ? ChangeToken.OnChange(root.GetReloadToken, () => Interlocked.Increment(ref _revision))
                : null;
        }

        internal long Revision => Interlocked.Read(ref _revision);

        public void Dispose()
        {
            _registration?.Dispose();
        }
    }
}
