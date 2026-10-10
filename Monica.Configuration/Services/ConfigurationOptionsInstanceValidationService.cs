using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

/// <summary>Evaluates explicitly supplied local instances without changing options creation or runtime policy.</summary>
internal sealed class ConfigurationOptionsInstanceValidationService(
    ConfigurationLocalDefinitionRegistry localRegistry,
    ConfigurationValidationCoordinator validationCoordinator) : IConfigurationOptionsInstanceValidationService
{
    /// <inheritdoc />
    public ConfigurationValidationReport Validate<TOptions>(TOptions options) where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(options);
        var definition = localRegistry.GetRequired(typeof(TOptions)).Definition;
        return ConfigurationValidationReportFactory.Create(definition,
            validationCoordinator.ValidateInstance(definition, options));
    }
}
