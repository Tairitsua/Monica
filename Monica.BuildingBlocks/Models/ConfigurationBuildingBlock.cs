using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Configuration.Models;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.BuildingBlocks.Services.Support;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Classifies the options accessor used by a consumer. Accessor choice alone does not determine reload behavior.
/// </summary>
public enum EConfigurationUsageType
{
    /// <summary>
    /// The accessor could not be identified.
    /// </summary>
    Unknown,
    /// <summary>
    /// Uses <see cref="IOptions{T}"/>.
    /// </summary>
    Offline,
    /// <summary>
    /// Uses <see cref="IOptionsSnapshot{T}"/>.
    /// </summary>
    OnlineSnapshot,
    /// <summary>
    /// Uses <see cref="IOptionsMonitor{T}"/>.
    /// </summary>
    OnlineMonitor
}

/// <summary>
/// Configuration class
/// </summary>
public class ConfigurationBuildingBlock : BuildingBlock
{
    private ConfigurationKind _kind;

    internal ConfigurationBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.Configuration, catalog)
    {
        DefinitionKey = shape.Type.FullName ?? shape.Type.Name;
    }

    /// <summary>
    /// Gets the Monica.Configuration definition key that corresponds to this options type.
    /// </summary>
    public string DefinitionKey { get; private set; }

    /// <summary>
    /// Configuration dependency details: record which building blocks use this configuration and how they use it
    /// </summary>
    public Dictionary<BuildingBlock, EConfigurationUsageType> ConfigurationDependencies { get; private set; } = new();

    /// <summary>
    /// Gets whether values require restart. Startup values are always frozen, including snapshot and monitor access.
    /// For Managed values, any <see cref="IOptions{T}"/> consumer requires restart; exclusively snapshot or monitor
    /// consumers permit reload, and absent or unknown usage returns <see langword="null"/>.
    /// </summary>
    public bool? IsOffline
    {
        get
        {
            if (_kind == ConfigurationKind.Startup || ConfigurationDependencies.Values.Contains(EConfigurationUsageType.Offline))
                return true;
            if (ConfigurationDependencies.Count > 0 && ConfigurationDependencies.Values.All(
                    usage => usage is EConfigurationUsageType.OnlineSnapshot or EConfigurationUsageType.OnlineMonitor))
                return false;
            return null;
        }
    }

    /// <summary>
    /// Gets the frozen Startup behavior or the Managed behavior inferred from discovered options access patterns.
    /// </summary>
    public ConfigurationReloadBehavior? InferredReloadBehavior => IsOffline switch
    {
        true => ConfigurationReloadBehavior.RequiresRestart,
        false => ConfigurationReloadBehavior.OnlineReloadable,
        _ => null
    };
    internal void RecordDependency(BuildingBlock dependentUnit, EConfigurationUsageType usageType)
    {
        ConfigurationDependencies[dependentUnit] = usageType;
    }

    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Prefix = "Options"
        };
    }

    internal static BuildingBlock Create(
        BusinessTypeShape shape,
        BuildingBlockCatalog catalog,
        ConfigurationAttribute configuration)
    {
        var unit = new ConfigurationBuildingBlock(shape, catalog);
        unit._kind = configuration.Kind;
        unit.CheckMode();
        unit.DefinitionKey = configuration.DefinitionKey ?? shape.Type.FullName ?? shape.Type.Name;
        unit.Title = configuration.DisplayName ?? unit.Title;
        unit.SetConfigurationDescription(configuration.Description);

        return unit;
    }
}
