using Monica.Configuration.Models;

namespace Monica.Configuration.Abstractions;

/// <summary>
/// Reads observed actual-options attempts independently of source-snapshot validation. Reading never creates options.
/// </summary>
public interface IConfigurationOptionsValidationDiagnostics
{
    /// <summary>Reads the latest observed default-options attempt for an owner.</summary>
    /// <param name="definitionKey">The owning definition key.</param>
    /// <returns>The latest immutable attempt, or null when none has been observed.</returns>
    ConfigurationOptionsValidationReport? GetLatestReport(string definitionKey);

    /// <summary>Reads the latest attempt for each observed owner.</summary>
    /// <returns>Immutable reports ordered by definition key.</returns>
    IReadOnlyList<ConfigurationOptionsValidationReport> GetReports();
}
