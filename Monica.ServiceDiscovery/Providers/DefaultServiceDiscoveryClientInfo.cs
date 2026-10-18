using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.ApplicationModel.Abstractions;
using Monica.Modules;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Models;

namespace Monica.ServiceDiscovery.Providers;

/// <summary>
/// Default registration center client information implementation
/// Read configuration from ModuleServiceDiscoveryOption and provide intelligent automatic detection of fallback values
/// </summary>
public class DefaultServiceDiscoveryClientInfo(
    IOptions<ModuleServiceDiscoveryOption> options,
    IMonicaApplicationOptions application,
    IServerAddressesFeature? serverAddressesFeature = null,
    IApplicationModel? applicationModel = null) : IServiceDiscoveryClientInfo
{
    private readonly ModuleServiceDiscoveryOption _options = options.Value;

    // Lazy initialization of basic service information (to avoid repeated expensive operations)
    private readonly Lazy<InstanceState> _baseServiceInfo = new(
        () => BuildBaseServiceInfo(options.Value, application, applicationModel));

    // Thread-safe local state management (similar to LeaderElectionService pattern)
    private readonly object _stateLock = new();
    private DateTime? _registrationTime;
    private DateTime? _lastHeartbeatTime;

    public DateTime? RegistrationTime
    {
        get
        {
            lock (_stateLock)
            {
                return _registrationTime;
            }
        }
    }

    public DateTime? LastHeartbeatTime
    {
        get
        {
            lock (_stateLock)
            {
                return _lastHeartbeatTime;
            }
        }
    }

    public void SetRegistrationTime(DateTime time)
    {
        lock (_stateLock)
        {
            // Only set when registering for the first time (do not change later)
            _registrationTime ??= time;
        }
    }

    public void UpdateLastHeartbeatTime(DateTime time)
    {
        lock (_stateLock)
        {
            _lastHeartbeatTime = time;
        }
    }

    public InstanceState GetServiceStatus(bool isHeartbeatInfo = false)
    {
        // Clone base information to avoid modifying cached instances
        var instanceState = CloneInstanceState(_baseServiceInfo.Value);

        if (isHeartbeatInfo)
        {
            return instanceState;
        }

        // Add metadata for full registration (not added for heartbeat)
        AddEnvironmentVariablesMetadata(instanceState);
        AddListeningAddressesMetadata(instanceState);

        return instanceState;
    }

    /// <summary>
    /// Builds the base instance state and normalizes persisted timestamps to UTC.
    /// </summary>
    private static InstanceState BuildBaseServiceInfo(
        ModuleServiceDiscoveryOption options,
        IMonicaApplicationOptions application,
        IApplicationModel? model)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        var assemblyName = entryAssembly?.GetName().Name ?? "Unknown";
        var appId = application.ResolveAppId(options.AppId, assemblyName);
        if (model is not null && model.AppId != appId)
            throw new InvalidOperationException("Service discovery AppId must match the unified application model.");
        var roles = model?.Roles.Values.Where(role => model.IsRoleActive(role.Id)).ToArray();
        var domains = roles?.Select(role => role.DomainId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];

        return new InstanceState
        {
            AppId = appId,
            InstanceId = options.FromInstance ?? GenerateFromInstance(),
            AppName = application.ResolveAppName(options.AppName, assemblyName),
            ProjectName = application.ResolveProjectName(options.ProjectName, assemblyName),
            DomainName = model is null ? application.ResolveDomainName(options.DomainName) : domains.Count == 1 ? domains[0] : null,
            DomainNames = domains,
            RoleIds = roles?.Select(role => role.Id).Order(StringComparer.Ordinal).ToList() ?? [],
            BuildTime = ResolveBuildTimeUtc(options.BuildTime, entryAssembly),
            AssemblyVersion = options.AssemblyVersion ?? GetAssemblyVersion(entryAssembly),
            ReleaseVersion = application.ResolveAppVersion(options.ReleaseVersion),
            DependentSubDomains = options.DependentSubDomains,
            RegistrationTime = DateTime.MinValue,  // 将在 CloneInstanceState 中使用本地状态覆盖
            LastHeartbeatTime = DateTime.MinValue   // 将在 CloneInstanceState 中使用本地状态覆盖
        };
    }

    /// <summary>
    /// Automatically generate FromInstance identifier, format: hostname:processId
    /// </summary>
    private static string GenerateFromInstance()
    {
        var hostname = Environment.GetEnvironmentVariable("COMPUTERNAME")
                       ?? Environment.GetEnvironmentVariable("HOSTNAME")
                       ?? Environment.MachineName;
        var processId = Environment.ProcessId;
        return $"{hostname}:{processId}";
    }

    /// <summary>
    /// Resolves the configured build time or falls back to the assembly file timestamp in UTC.
    /// </summary>
    private static DateTime ResolveBuildTimeUtc(DateTime? configuredBuildTime, Assembly? assembly)
    {
        return configuredBuildTime is { } value
            ? NormalizeUtc(value)
            : GetBuildTimeUtc(assembly);
    }

    /// <summary>
    /// Extracts the build time from the assembly file last write timestamp in UTC.
    /// </summary>
    private static DateTime GetBuildTimeUtc(Assembly? assembly)
    {
        if (assembly == null) return DateTime.MinValue;

        try
        {
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location)) return DateTime.MinValue;

            return File.GetLastWriteTimeUtc(location);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Normalizes configured values so the stored timestamp is always UTC.
    /// </summary>
    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
        };
    }

    /// <summary>
    /// Extract assembly version number from FileVersionInfo
    /// </summary>
    private static string? GetAssemblyVersion(Assembly? assembly)
    {
        if (assembly == null) return null;

        try
        {
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location)) return null;

            var versionInfo = FileVersionInfo.GetVersionInfo(location);
            return versionInfo.ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clone the InstanceState to avoid modifying the cached instance
    /// </summary>
    private InstanceState CloneInstanceState(InstanceState original)
    {
        return new InstanceState
        {
            AppId = original.AppId,
            DomainNames = original.DomainNames.ToList(),
            RoleIds = original.RoleIds.ToList(),
            InstanceId = original.InstanceId,
            AppName = original.AppName,
            ProjectName = original.ProjectName,
            DomainName = original.DomainName,
            BuildTime = original.BuildTime,
            AssemblyVersion = original.AssemblyVersion,
            ReleaseVersion = original.ReleaseVersion,
            DependentSubDomains = original.DependentSubDomains?.ToList(),
            RegistrationTime = RegistrationTime ?? DateTime.MinValue,  // 使用本地状态
            LastHeartbeatTime = LastHeartbeatTime ?? DateTime.MinValue,  // 使用本地状态
            Metadata = new Dictionary<string, string>() // 从空元数据开始
        };
    }

    /// <summary>
    /// Add environment variables as metadata based on configuration
    /// </summary>
    private void AddEnvironmentVariablesMetadata(InstanceState instanceState)
    {
        foreach (var envKey in _options.MetadataEnvironmentVariables)
        {
            var envValue = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrEmpty(envValue))
            {
                instanceState.Metadata[envKey] = envValue;
            }
        }
    }

    /// <summary>
    /// If enabled by configuration, add listening address as metadata
    /// </summary>
    private void AddListeningAddressesMetadata(InstanceState instanceState)
    {
        if (_options.IncludeListeningAddresses &&
            serverAddressesFeature?.Addresses != null &&
            serverAddressesFeature.Addresses.Any())
        {
            var addresses = string.Join(";", serverAddressesFeature.Addresses);
            instanceState.Metadata[IServiceDiscoveryClientInfo.LISTENING_ADDRESS_METADATA_KEY] = addresses;
        }
    }
}
