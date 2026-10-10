using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Creates first-run values from actual local defaults and the source-faithful bootstrap stack.
/// Executable types are supplied by local discovery; published type names are never loaded.
/// </summary>
public sealed class ConfigurationEffectiveValueSeedFactory
{
    private readonly ConfigurationRuntimeContext _runtimeContext;
    private readonly ConfigurationLocalDefinitionRegistry _localRegistry;
    private readonly ConfigurationObjectMaterializer _materializer;

    internal ConfigurationEffectiveValueSeedFactory(ConfigurationRuntimeContext runtimeContext,
        ConfigurationLocalDefinitionRegistry localRegistry, ConfigurationObjectMaterializer materializer)
    {
        _runtimeContext = runtimeContext;
        _localRegistry = localRegistry;
        _materializer = materializer;
    }

    /// <summary>Creates complete seed JSON through production binding and declared configuration aliases.</summary>
    /// <param name="definition">The locally discovered owner.</param>
    /// <returns>The full bound value, including constructor defaults and explicit empty or null values.</returns>
    public string CreateSeedJson(ConfigurationDefinition definition)
    {
        _localRegistry.GetRequired(definition);
        return _materializer.Snapshot(definition, _materializer.Materialize(definition, _runtimeContext.Configuration));
    }

    /// <summary>Reads a schema-owned source value without executing remote object types.</summary>
    /// <param name="node">The schema node to project.</param>
    /// <param name="configurationPath">The Microsoft configuration path to read.</param>
    /// <returns>A shape-preserving JSON source snapshot.</returns>
    public string CreateRuntimeJson(ConfigurationNodeDefinition node, string configurationPath)
    {
        return _materializer.ReadConfigurationJson(new ConfigurationDefinition
        {
            DefinitionKey = "source-snapshot",
            SectionPath = configurationPath,
            DisplayName = node.Name,
            ClrTypeName = node.ClrTypeName,
            FromProject = "source-snapshot",
            SchemaHash = string.Empty,
            Root = node
        }, _runtimeContext.Configuration);
    }
}
