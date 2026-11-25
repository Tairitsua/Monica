using Monica.Configuration.Models;

namespace Monica.Configuration.Abstractions;

/// <summary>
/// Builds source-aware runtime validation reports for Monica-managed configuration definitions.
/// </summary>
public interface IConfigurationRuntimeValidationService
{
    /// <summary>
    /// Builds a report for all applicable definitions, including failures isolated to an assigned role.
    /// </summary>
    /// <returns>The validation report.</returns>
    ConfigurationValidationReport GetReport();

    /// <summary>Builds the host enforcement report, excluding Startup definitions already quarantined during role preflight.</summary>
    ConfigurationValidationReport GetEnforcementReport() => GetReport();

    /// <summary>
    /// Builds a validation report for one local configuration definition.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <returns>The validation report.</returns>
    ConfigurationValidationReport GetReport(string definitionKey);
}
