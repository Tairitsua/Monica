using Monica.BuildingBlocks.Abstractions;
using Monica.Tool.Extensions;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Monica.BuildingBlocks.Providers.Serilog;

/// <summary>
/// Provides Serilog configuration extensions for host-scoped Building Blocks metadata.
/// </summary>
public static class DynamicSourceContextLoggerConfigurationExtensions
{
    /// <summary>
    /// Replaces source-context values with building-block display names when metadata is available.
    /// </summary>
    /// <param name="enrichmentConfiguration">The Serilog enrichment configuration.</param>
    /// <param name="catalog">The building-block catalog resolved from the Monica host being configured.</param>
    /// <returns>The owning logger configuration.</returns>
    public static LoggerConfiguration WithDynamicSourceContext(
        this LoggerEnrichmentConfiguration enrichmentConfiguration,
        IBuildingBlockCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(enrichmentConfiguration);
        ArgumentNullException.ThrowIfNull(catalog);
        return enrichmentConfiguration.With(new DynamicSourceEnricher(catalog));
    }
}

/// <summary>
/// Enriches Serilog events with host-scoped building-block display metadata.
/// </summary>
public sealed class DynamicSourceEnricher(IBuildingBlockCatalog catalog) : ILogEventEnricher
{
    private const string PROPERTIES_TEMPLATE_NAME = "Properties";

    /// <summary>
    /// Gets the Serilog source-context property name.
    /// </summary>
    public const string SOURCE_CONTEXT_TEMPLATE_NAME = "SourceContext";

    /// <summary>
    /// Gets the property name used to mark a building-block source.
    /// </summary>
    public const string SOURCE_MARKER_TEMPLATE_NAME = "BuildingBlockSource";

    /// <summary>
    /// Enrich the log event.
    /// </summary>
    /// <param name="logEvent">The log event to enrich.</param>
    /// <param name="propertyFactory">Factory for creating new properties to add to the event.</param>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var properties = (Dictionary<string, LogEventPropertyValue>?)logEvent.GetPropertyValue(PROPERTIES_TEMPLATE_NAME);
        if (properties?.TryGetValue(SOURCE_CONTEXT_TEMPLATE_NAME, out var propertyValue) is true)
        {
            var name = catalog.FindByFullName(propertyValue.ToString().Trim('\"'))?.MetadataTitle;

            if (name != null)
            {
                properties[SOURCE_CONTEXT_TEMPLATE_NAME] = new ScalarValue(name);
                properties.Add(SOURCE_MARKER_TEMPLATE_NAME, new ScalarValue("[T:B]"));
                logEvent.AddOrUpdateProperty(
                    new LogEventProperty(PROPERTIES_TEMPLATE_NAME, new ScalarValue(properties)));
            }
        }
    }
}
