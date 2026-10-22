using System.Diagnostics;
using System.Reflection;
using Monica.Core.Modularity.Abstractions;

namespace Monica.Core;

/// <summary>
/// Read-only view of shared Monica application identity defaults.
/// </summary>
public interface IMonicaApplicationOptions
{
    /// <summary>
    /// Gets the application project name.
    /// </summary>
    string? ProjectName { get; }

    /// <summary>
    /// Gets the application identifier.
    /// </summary>
    string? AppId { get; }

    /// <summary>
    /// Gets the display name of the application.
    /// </summary>
    string? AppName { get; }

    /// <summary>
    /// Gets the display or release version of the application.
    /// </summary>
    string? AppVersion { get; }

    /// <summary>
    /// Gets the application domain or subdomain name.
    /// </summary>
    string? DomainName { get; }

    /// <summary>
    /// Resolves a project name using module configuration, global configuration, and an optional local fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when neither module nor global configuration provides a value.</param>
    /// <returns>The resolved project name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no project name can be resolved.</exception>
    string ResolveProjectName(string? configuredValue = null, string? fallback = null);

    /// <summary>
    /// Resolves an application identifier using module configuration, global configuration, and project-name fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when no application identity value is configured.</param>
    /// <returns>The resolved application identifier.</returns>
    string ResolveAppId(string? configuredValue = null, string? fallback = null);

    /// <summary>
    /// Resolves an application display name using module configuration, global configuration, and application-identity fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when no display name is configured.</param>
    /// <returns>The resolved application display name.</returns>
    string ResolveAppName(string? configuredValue = null, string? fallback = null);

    /// <summary>
    /// Resolves an application version using module configuration, global configuration, and an optional local fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when no version is configured.</param>
    /// <returns>The resolved application version, or <see langword="null"/> when no value is configured.</returns>
    string? ResolveAppVersion(string? configuredValue = null, string? fallback = null);

    /// <summary>
    /// Resolves an application domain name using module configuration and global configuration.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <returns>The resolved domain name, or <see langword="null"/> when no value is configured.</returns>
    string? ResolveDomainName(string? configuredValue = null);
}

/// <summary>
/// Mutable configuration model for shared Monica application identity defaults.
/// Configure it through <see cref="IMonicaBuilder.ConfigureApplication"/>.
/// </summary>
public sealed class MonicaApplicationOptions : IMonicaApplicationOptions
{
    /// <summary>
    /// Gets or sets the application project name.
    /// Defaults to the entry assembly name and is the root fallback for application identity values.
    /// </summary>
    public string? ProjectName { get; set; } = Assembly.GetEntryAssembly()?.GetName().Name;

    /// <summary>
    /// Gets or sets the application identifier.
    /// When not configured, modules fall back to <see cref="ProjectName"/>.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Gets or sets the display name of the application.
    /// When not configured, modules fall back to <see cref="ProjectName"/>.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Gets or sets the display or release version of the application.
    /// Defaults to the entry assembly informational or product version when available.
    /// </summary>
    public string? AppVersion { get; set; } = ResolveEntryAssemblyAppVersion();

    /// <summary>
    /// Gets or sets the application domain or subdomain name.
    /// Modules treat this as unset when it is <see langword="null"/> or whitespace.
    /// </summary>
    public string? DomainName { get; set; }

    /// <summary>
    /// Resolves a project name using module configuration, global configuration, and an optional local fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when neither module nor global configuration provides a value.</param>
    /// <returns>The resolved project name.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no project name can be resolved.</exception>
    public string ResolveProjectName(string? configuredValue = null, string? fallback = null)
    {
        return Normalize(configuredValue)
               ?? Normalize(ProjectName)
               ?? Normalize(fallback)
               ?? throw new InvalidOperationException("Application project name must be configured.");
    }

    /// <summary>
    /// Resolves an application identifier using module configuration, global configuration, and project-name fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when no application identity value is configured.</param>
    /// <returns>The resolved application identifier.</returns>
    public string ResolveAppId(string? configuredValue = null, string? fallback = null)
    {
        return Normalize(configuredValue)
               ?? Normalize(AppId)
               ?? ResolveProjectName(fallback: fallback);
    }

    /// <summary>
    /// Resolves an application display name using module configuration, global configuration, and application-identity fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when no display name is configured.</param>
    /// <returns>The resolved application display name.</returns>
    public string ResolveAppName(string? configuredValue = null, string? fallback = null)
    {
        return Normalize(configuredValue)
               ?? Normalize(AppName)
               ?? ResolveAppId(fallback: fallback);
    }

    /// <summary>
    /// Resolves an application version using module configuration, global configuration, and an optional local fallback.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <param name="fallback">The local fallback used when no version is configured.</param>
    /// <returns>The resolved application version, or <see langword="null"/> when no value is configured.</returns>
    public string? ResolveAppVersion(string? configuredValue = null, string? fallback = null)
    {
        return Normalize(configuredValue)
               ?? Normalize(AppVersion)
               ?? Normalize(fallback);
    }

    /// <summary>
    /// Resolves an application domain name using module configuration and global configuration.
    /// </summary>
    /// <param name="configuredValue">The module-specific value, when explicitly configured.</param>
    /// <returns>The resolved domain name, or <see langword="null"/> when no value is configured.</returns>
    public string? ResolveDomainName(string? configuredValue = null)
    {
        return Normalize(configuredValue) ?? Normalize(DomainName);
    }

    /// <summary>
    /// Normalizes a string identity value and treats whitespace as unset.
    /// </summary>
    /// <param name="value">The value to normalize.</param>
    /// <returns>The trimmed value, or <see langword="null"/> when unset.</returns>
    public static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ResolveEntryAssemblyAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null)
        {
            return null;
        }

        var informationalVersion = Normalize(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        if (informationalVersion is not null)
        {
            return informationalVersion;
        }

        try
        {
            var location = assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                return null;
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(location);
            return Normalize(versionInfo.ProductVersion);
        }
        catch
        {
            return null;
        }
    }
}
