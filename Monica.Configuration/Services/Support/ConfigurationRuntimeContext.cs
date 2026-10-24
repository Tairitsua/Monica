using Microsoft.Extensions.Configuration;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Holds the host configuration instance captured during Monica.Configuration module initialization.
/// </summary>
public sealed class ConfigurationRuntimeContext
{
    private IConfiguration? _configuration;

    internal ConfigurationStartupOptionsReader? StartupOptions { get; set; }

    /// <summary>
    /// Gets the captured host configuration used by Monica.Configuration internals.
    /// </summary>
    public IConfiguration Configuration => _configuration
        ?? throw new InvalidOperationException(
            "Monica.Configuration has not captured the host configuration. Ensure the module is registered through the host builder before resolving configuration services.");

    /// <summary>
    /// Gets the captured host configuration as a provider root when provider inspection is available.
    /// </summary>
    public IConfigurationRoot? Root => Configuration as IConfigurationRoot;

    /// <summary>
    /// Captures the host configuration instance that Monica.Configuration augments and inspects.
    /// </summary>
    /// <param name="configuration">The host configuration instance.</param>
    internal void Capture(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }
}
