namespace Monica.Configuration.Models;

/// <summary>Identifies the authority that owns a configuration definition's values.</summary>
public enum ConfigurationKind
{
    /// <summary>Values are owned by the Monica effective-value store and its reviewed mutation workflow.</summary>
    Managed,

    /// <summary>Values come from deployment inputs, are frozen during composition, and require restart to change.</summary>
    Startup
}
