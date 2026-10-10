using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Bridges Monica runtime configuration validation into Microsoft.Extensions.Options.
/// </summary>
/// <typeparam name="TOptions">The managed options type.</typeparam>
internal sealed class MonicaConfigurationOptionsValidator<TOptions>(
    IConfigurationDefinitionRegistry definitions,
    ConfigurationValidationCoordinator validationCoordinator,
    ConfigurationOptionsValidationDiagnostics diagnostics,
    ConfigurationRuntimeValidationBehavior behavior,
    string definitionKey)
    : IValidateOptions<TOptions>
    where TOptions : class
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (name is not null && !string.Equals(name, Options.DefaultName, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        try
        {
            var definition = definitions.GetRequired(definitionKey);
            var report = diagnostics.Record(definition, validationCoordinator.ValidateInstance(definition, options));
            return report.IsValid || behavior == ConfigurationRuntimeValidationBehavior.DiagnosticOnly
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(report.Issues.Select(ConfigurationRuntimeValidationMessageFormatter.FormatOptionsIssue));
        }
        catch (ConfigurationValidationExecutionException fault)
        {
            diagnostics.RecordFault(definitionKey, fault);
            throw;
        }
    }
}
