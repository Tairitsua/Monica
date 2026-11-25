using Monica.Configuration.Models;

namespace Monica.Configuration.Exceptions;

/// <summary>Raised on access to frozen options whose local owner was inactive or failed startup validation.</summary>
public sealed class StartupConfigurationUnavailableException(string definitionKey, ConfigurationStartupStatus status)
    : InvalidOperationException($"Startup configuration '{definitionKey}' is unavailable on this instance ({status}).")
{
    /// <summary>The stable configuration definition key.</summary>
    public string DefinitionKey { get; } = definitionKey;

    /// <summary>The process-local reason for withholding a typed options value.</summary>
    public ConfigurationStartupStatus Status { get; } = status;
}
