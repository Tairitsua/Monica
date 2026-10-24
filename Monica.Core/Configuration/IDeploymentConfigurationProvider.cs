namespace Monica.Core.Configuration;

/// <summary>Identifies a configuration provider whose source is owned by deployment.</summary>
/// <remarks>
/// Configuration tools must treat this source as read-only, even when its underlying file is writable. Changing
/// deployment inputs belongs to deployment tooling; this marker does not prevent the provider accepting validated
/// external reloads or imply that startup consumers adopt those changes without restarting.
/// </remarks>
public interface IDeploymentConfigurationProvider
{
}
