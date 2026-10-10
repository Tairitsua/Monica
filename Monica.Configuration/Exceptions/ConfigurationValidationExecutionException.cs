using Monica.Configuration.Models;

namespace Monica.Configuration.Exceptions;

/// <summary>
/// Identifies a fatal validation contract or execution fault using safe diagnostics.
/// </summary>
/// <remarks>
/// The exception message and public properties never include a value, user exception text, or candidate JSON.
/// User exception causes are deliberately discarded so ordinary recursive formatters and logging remain safe.
/// </remarks>
public sealed class ConfigurationValidationExecutionException : Exception
{
    /// <summary>Creates a display-safe operational fault.</summary>
    /// <param name="definitionKey">The owning definition key.</param>
    /// <param name="logicalPath">The safe schema path at which execution stopped.</param>
    /// <param name="stage">The fixed framework stage identifier.</param>
    /// <param name="kind">The contract or execution fault category.</param>
    /// <param name="innerException">The execution cause, deliberately discarded at this public boundary.</param>
    public ConfigurationValidationExecutionException(
        string definitionKey,
        LogicalPath logicalPath,
        string stage,
        ConfigurationValidationIssueKind kind = ConfigurationValidationIssueKind.Execution,
        Exception? innerException = null)
        : base($"Configuration validation could not complete during '{stage}'.")
    {
        DefinitionKey = definitionKey;
        LogicalPath = logicalPath;
        Stage = stage;
        Kind = kind;
    }

    /// <summary>Gets the owning definition key.</summary>
    public string DefinitionKey { get; }
    /// <summary>Gets the schema path where validation stopped.</summary>
    public LogicalPath LogicalPath { get; }
    /// <summary>Gets the fixed framework stage identifier.</summary>
    public string Stage { get; }
    /// <summary>Gets the operational fault category.</summary>
    public ConfigurationValidationIssueKind Kind { get; }
}
