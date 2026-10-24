using Monica.Configuration.Annotations;
using Monica.Configuration.Models;

namespace Monica.Reference.Api.Configurations;

/// <summary>
/// Selects optional diagnostic modules before the reference host registers its services.
/// </summary>
[Configuration(
    "ReferenceUI",
    Kind = ConfigurationKind.Startup,
    DisplayName = "Reference host diagnostics",
    Description = "Diagnostic module selection is fixed until the host restarts.",
    Category = "Host")]
public sealed class ReferenceUIOptions
{
    /// <summary>
    /// Gets or sets whether the host composes the module and configuration diagnostic pages.
    /// Defaults to false; changing a source requires restarting the host.
    /// </summary>
    public bool EnableDiagnostics { get; set; }
}
