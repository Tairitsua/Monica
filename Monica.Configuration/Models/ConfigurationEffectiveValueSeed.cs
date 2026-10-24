namespace Monica.Configuration.Models;

/// <summary>
/// Carries the seed document used when an effective configuration value does not exist yet.
/// </summary>
public sealed class ConfigurationEffectiveValueSeed
{
    private readonly Lazy<string> _seedJson;

    /// <summary>
    /// Creates an eager seed request.
    /// </summary>
    /// <param name="definition">The configuration definition that owns the document.</param>
    /// <param name="seedJson">The JSON document to persist when the definition is missing.</param>
    public ConfigurationEffectiveValueSeed(ConfigurationDefinition definition, string seedJson)
        : this(definition, () => seedJson)
    {
        ArgumentNullException.ThrowIfNull(seedJson);
    }

    /// <summary>
    /// Creates a lazy seed request whose JSON is generated only when the store discovers a missing document.
    /// </summary>
    /// <param name="definition">The configuration definition that owns the document.</param>
    /// <param name="seedJsonFactory">Factory invoked at most once when the document must be created.</param>
    public ConfigurationEffectiveValueSeed(ConfigurationDefinition definition, Func<string> seedJsonFactory)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (definition.Kind == ConfigurationKind.Startup)
            throw new InvalidOperationException($"Startup configuration '{definition.DefinitionKey}' cannot be seeded into a managed store.");
        ArgumentNullException.ThrowIfNull(seedJsonFactory);
        _seedJson = new Lazy<string>(
            () => seedJsonFactory()
                  ?? throw new InvalidOperationException(
                      $"The seed factory for configuration definition '{Definition.DefinitionKey}' returned null."),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the configuration definition that owns the effective value document.
    /// </summary>
    public ConfigurationDefinition Definition { get; }

    /// <summary>
    /// Materializes the JSON document used to initialize the effective value when it is missing.
    /// </summary>
    /// <remarks>
    /// Stores must call this method only after they have established that the document does not already exist. The
    /// result is generated at most once for this request.
    /// </remarks>
    /// <returns>The seed JSON document.</returns>
    public string MaterializeSeedJson()
    {
        return _seedJson.Value;
    }
}
