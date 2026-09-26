namespace Monica.AI.Configuration;

/// <summary>Startup behavior when a registered provider or model configuration fails validation.</summary>
public enum AIConfigurationValidationMode
{
    /// <summary>
    /// Invalid configuration disables the affected provider instead of failing startup. The validation reasons
    /// surface as provider configuration errors in the management UI and provider diagnostics. Runtime settings
    /// edits still validate strictly.
    /// </summary>
    Disable,

    /// <summary>
    /// Invalid configuration throws while the host is starting, so misconfiguration is fixed before deployment.
    /// </summary>
    Throw
}
