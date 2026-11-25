namespace Monica.Configuration.Models;

/// <summary>Whether this process admitted a frozen Startup options instance.</summary>
public enum ConfigurationStartupStatus
{
    /// <summary>The frozen options are valid and available.</summary>
    Valid,
    /// <summary>No assigned role requires this explicitly scoped definition.</summary>
    NotRequiredOnHost,
    /// <summary>The configuration failed validation and its assigned owners were quarantined.</summary>
    Faulted
}
