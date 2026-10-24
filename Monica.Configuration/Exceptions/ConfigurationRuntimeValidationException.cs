using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Exceptions;

/// <summary>
/// Thrown when runtime enforcement or Startup composition rejects configuration validation.
/// </summary>
public sealed class ConfigurationRuntimeValidationException : InvalidOperationException
{
    /// <summary>Creates a managed runtime fail-fast validation error.</summary>
    /// <param name="report">The safe structured report that rejected the value.</param>
    public ConfigurationRuntimeValidationException(ConfigurationValidationReport report) : this(report, startup: false) { }

    internal ConfigurationRuntimeValidationException(ConfigurationValidationReport report, bool startup)
        : base(startup ? ConfigurationRuntimeValidationMessageFormatter.FormatStartupReport(report)
            : ConfigurationRuntimeValidationMessageFormatter.FormatFailFastReport(report)) => Report = report;

    /// <summary>
    /// Gets the structured validation report that triggered fail-fast enforcement.
    /// </summary>
    public ConfigurationValidationReport Report { get; }
}
