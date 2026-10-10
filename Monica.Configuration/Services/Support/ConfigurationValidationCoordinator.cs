using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Coordinates mutation-time validation.
/// </summary>
internal sealed class ConfigurationValidationCoordinator(
    ConfigurationValueValidationEngine validationEngine,
    ConfigurationLocalDefinitionRegistry localRegistry,
    ConfigurationObjectMaterializer materializer,
    ConfigurationObjectValidationWalker objectWalker)
{
    private static readonly ConfigurationValueValidationOptions MUTATION_OPTIONS = new()
    {
        TreatNonNullableScalarsAsRequired = true,
        RejectUnknownObjectProperties = true
    };

    // Captured historical values legitimately predate the current schema: properties may have been
    // removed from the definition since capture. Unknown properties never reach persistence planning
    // (the planner only walks schema-declared nodes), so they are tolerated here instead of rejecting
    // the rollback. Missing non-nullable scalars stay fatal because restoring would silently fall back
    // to a default the operator never reviewed.
    private static readonly ConfigurationValueValidationOptions CAPTURED_VALUE_OPTIONS = new()
    {
        TreatNonNullableScalarsAsRequired = true,
        RejectUnknownObjectProperties = false
    };

    /// <summary>
    /// Validates a mutation against the current schema constraints.
    /// </summary>
    /// <param name="definition">The target definition.</param>
    /// <param name="request">The mutation request.</param>
    /// <param name="validateFragment">Whether to validate fragment constraints in addition to the schema identity and target path.</param>
    public void Validate(ConfigurationDefinition definition, ConfigurationMutationRequest request, bool validateFragment = true)
    {
        if (request.ExpectedSchemaVersion > 0 && request.ExpectedSchemaVersion != definition.SchemaVersion)
        {
            throw new ConfigurationSchemaMismatchException(
                $"Expected schema version {request.ExpectedSchemaVersion}, but definition '{definition.DefinitionKey}' is version {definition.SchemaVersion}.");
        }

        if (request.ExpectedSchemaHash is { Length: > 0 } expectedSchemaHash
            && !string.Equals(expectedSchemaHash, definition.SchemaHash, StringComparison.Ordinal))
        {
            throw new ConfigurationSchemaMismatchException(
                $"The reviewed schema fingerprint for definition '{definition.DefinitionKey}' no longer matches the active schema.");
        }

        var target = ResolveTargetNode(definition, request.LogicalPath);
        if (!validateFragment) return;
        var issues = request.MutationKind == ConfigurationMutationKind.Remove
            ? validationEngine.ValidateRemoval(target, request.LogicalPath, MUTATION_OPTIONS)
            : ValidateValue(target, request.LogicalPath, request.Value.Json);

        if (issues.FirstOrDefault() is { } issue)
        {
            throw new ConfigurationValidationFailedException($"{issue.LogicalPath.ToCanonicalString()}: {issue.Message}");
        }
    }

    /// <summary>Validates complete reviewed JSON before defaults can conceal required raw fields.</summary>
    internal ConfigurationCompleteValidationResult ValidateCompleteValue(ConfigurationDefinition definition, string json,
        ConfigurationValidationProfile profile = ConfigurationValidationProfile.Mutation)
    {
        using var document = JsonDocument.Parse(json);
        var options = profile switch
        {
            ConfigurationValidationProfile.Mutation => MUTATION_OPTIONS,
            ConfigurationValidationProfile.CapturedValue => CAPTURED_VALUE_OPTIONS,
            _ => new ConfigurationValueValidationOptions()
        };
        var rawIssues = validationEngine.Validate(definition.Root, LogicalPath.Root, document.RootElement, options);
        if (rawIssues.Any(issue => issue.BlocksMaterialization))
            return Result(definition, ConfigurationValidationCoverage.Failed, rawIssues);
        if (!localRegistry.TryGet(definition.DefinitionKey, out _))
            return Result(definition, definition.ValidationContract.Capability == ConfigurationValidationCapability.PortableOnly
                && !string.IsNullOrWhiteSpace(definition.ValidationContract.Revision)
                && HasCompletePortableShape(definition.Root, document.RootElement)
                ? ConfigurationValidationCoverage.Complete : ConfigurationValidationCoverage.SchemaOnly, rawIssues);
        var builder = new ConfigurationBuilder().Add(new ConfigurationValueProjectionSource(materializer.Project(definition, json)));
        var root = builder.Build();
        try
        {
            var instance = materializer.Materialize(definition, root);
            var instanceResult = ValidateInstanceCore(definition, instance, ConfigurationValidationScope.CompleteAggregate);
            // Raw checks own mutation structure; bound checks also cover omitted CLR defaults. Exact duplicates are avoided.
            var issues = MergeIssues(rawIssues, instanceResult.Issues);
            return instanceResult with { Issues = Array.AsReadOnly(issues) };
        }
        finally { (root as IDisposable)?.Dispose(); }
    }

    /// <summary>Validates the complete effective root with source precedence and CLR defaults.</summary>
    internal ConfigurationCompleteValidationResult ValidateConfiguration(ConfigurationDefinition definition,
        IConfiguration configuration, ConfigurationValidationProfile profile = ConfigurationValidationProfile.Runtime)
    {
        if (!localRegistry.TryGet(definition.DefinitionKey, out _))
        {
            using var raw = JsonDocument.Parse(materializer.ReadConfigurationJson(definition, configuration));
            var options = profile switch
            {
                ConfigurationValidationProfile.Mutation => MUTATION_OPTIONS,
                ConfigurationValidationProfile.CapturedValue => CAPTURED_VALUE_OPTIONS,
                _ => new ConfigurationValueValidationOptions()
            };
            var complete = definition.ValidationContract.Capability == ConfigurationValidationCapability.PortableOnly
                && !string.IsNullOrWhiteSpace(definition.ValidationContract.Revision)
                && HasCompletePortableShape(definition.Root, raw.RootElement);
            return Result(definition, complete ? ConfigurationValidationCoverage.Complete : ConfigurationValidationCoverage.SchemaOnly,
                validationEngine.Validate(definition.Root, LogicalPath.Root, raw.RootElement, options));
        }
        var instance = materializer.Materialize(definition, configuration);
        var result = ValidateInstanceCore(definition, instance, ConfigurationValidationScope.CompleteAggregate);
        // Binding may normalize collections (for example, HashSet removes duplicates). Preserve source
        // constraints while checking missing members on the complete instance with its CLR defaults.
        using var source = JsonDocument.Parse(materializer.ReadConfigurationJson(definition, configuration));
        var rawIssues = validationEngine.Validate(definition.Root, LogicalPath.Root, source.RootElement,
            new ConfigurationValueValidationOptions()).Where(issue => !issue.IsMissing);
        return result with { Issues = Array.AsReadOnly(MergeIssues(rawIssues, result.Issues)) };
    }

    private static ConfigurationValueValidationIssue[] MergeIssues(IEnumerable<ConfigurationValueValidationIssue> raw,
        IEnumerable<ConfigurationValueValidationIssue> bound)
    {
        var schemaIdentities = new HashSet<(string Path, string Message)>();
        return raw.Concat(bound).Where(issue => issue.Kind != ConfigurationValidationIssueKind.Schema
            || schemaIdentities.Add((issue.LogicalPath.ToCanonicalString(), issue.Message))).ToArray();
    }

    /// <summary>Validates the supplied actual options value without rebinding or resolving options.</summary>
    internal ConfigurationCompleteValidationResult ValidateInstance(ConfigurationDefinition definition, object options)
        => ValidateInstanceCore(definition, options, ConfigurationValidationScope.ActualOptions);

    /// <summary>Checks a partial external contribution without allowing undeclared fields to disappear during binding.</summary>
    internal IReadOnlyList<ConfigurationValueValidationIssue> ValidateSourceContribution(ConfigurationDefinition definition, string json)
    {
        using var document = JsonDocument.Parse(json);
        return validationEngine.Validate(definition.Root, LogicalPath.Root, document.RootElement,
            new ConfigurationValueValidationOptions { RejectUnknownObjectProperties = true })
            .Where(issue => issue.BlocksProjection).ToArray();
    }

    /// <summary>Reads the complete bound effective value for semantic before/after comparisons.</summary>
    internal string ReadEffectiveJson(ConfigurationDefinition definition, IConfiguration configuration)
        => localRegistry.TryGet(definition.DefinitionKey, out _)
            ? materializer.Snapshot(definition, materializer.Materialize(definition, configuration))
            : materializer.ReadConfigurationJson(definition, configuration);

    /// <summary>Projects a stored document with the same schema pruning and shape semantics as the runtime provider.</summary>
    internal ConfigurationValueProjection ProjectStoredValue(ConfigurationDefinition definition, string json)
        => materializer.Project(definition, json);

    private static bool HasCompletePortableShape(ConfigurationNodeDefinition schema, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (schema.NodeKind == ConfigurationNodeKind.Object && value.ValueKind == JsonValueKind.Object)
            return schema.Children.All(child => value.EnumerateObject().Any(property =>
                string.Equals(property.Name, child.Name, StringComparison.OrdinalIgnoreCase)
                && HasCompletePortableShape(child, property.Value)));
        if (schema.ListTemplate is { } list && value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().All(item => HasCompletePortableShape(list.ItemTemplate, item));
        if (schema.DictionaryTemplate is { } dictionary && value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().All(property => HasCompletePortableShape(dictionary.ValueTemplate, property.Value));
        return schema.NodeKind == ConfigurationNodeKind.Scalar;
    }

    private ConfigurationCompleteValidationResult ValidateInstanceCore(ConfigurationDefinition definition, object options,
        ConfigurationValidationScope scope)
    {
        var descriptor = localRegistry.GetRequired(definition);
        using var snapshot = JsonDocument.Parse(materializer.Snapshot(definition, options));
        var schemaIssues = validationEngine.Validate(definition.Root, LogicalPath.Root, snapshot.RootElement,
            new ConfigurationValueValidationOptions());
        var codeIssues = objectWalker.Validate(descriptor, options);
        return Result(definition, ConfigurationValidationCoverage.Complete, schemaIssues.Concat(codeIssues)) with { Scope = scope };
    }

    private static ConfigurationCompleteValidationResult Result(ConfigurationDefinition definition,
        ConfigurationValidationCoverage coverage, IEnumerable<ConfigurationValueValidationIssue> issues)
        => new()
        {
            Coverage = coverage,
            ValidationRevision = definition.ValidationContract.Revision,
            Issues = Array.AsReadOnly(issues.ToArray())
        };

    /// <summary>
    /// Validates a complete JSON value against the definition's current schema and returns every issue.
    /// </summary>
    /// <param name="definition">The current configuration definition.</param>
    /// <param name="json">The complete root JSON value.</param>
    /// <returns>All schema validation issues found in the value.</returns>
    public IReadOnlyList<ConfigurationValueValidationIssue> ValidateValue(
        ConfigurationDefinition definition,
        string json)
    {
        return ValidateValue(definition, LogicalPath.Root, json);
    }

    /// <summary>
    /// Validates a captured historical value against the definition's current schema using rollback tolerance.
    /// </summary>
    /// <param name="definition">The current configuration definition.</param>
    /// <param name="json">The captured complete root JSON value.</param>
    /// <returns>
    /// The remaining hard incompatibilities. Unknown object properties are ignored because persistence
    /// planning only writes schema-declared paths.
    /// </returns>
    public IReadOnlyList<ConfigurationValueValidationIssue> ValidateCapturedValue(
        ConfigurationDefinition definition,
        string json)
    {
        var target = ResolveTargetNode(definition, LogicalPath.Root);
        using var document = JsonDocument.Parse(json);
        return validationEngine.Validate(target, LogicalPath.Root, document.RootElement, CAPTURED_VALUE_OPTIONS);
    }

    /// <summary>
    /// Validates a complete JSON value for one definition scope and returns every issue.
    /// </summary>
    /// <param name="definition">The current configuration definition.</param>
    /// <param name="logicalPath">The logical path whose complete value is represented by <paramref name="json"/>.</param>
    /// <param name="json">The complete JSON value for the target scope.</param>
    /// <returns>All schema validation issues found in the value.</returns>
    public IReadOnlyList<ConfigurationValueValidationIssue> ValidateValue(
        ConfigurationDefinition definition,
        LogicalPath logicalPath,
        string json)
    {
        var target = ResolveTargetNode(definition, logicalPath);
        return ValidateValue(target, logicalPath, json);
    }

    private IReadOnlyList<ConfigurationValueValidationIssue> ValidateValue(
        ConfigurationNodeDefinition target,
        LogicalPath logicalPath,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return validationEngine.Validate(target, logicalPath, document.RootElement, MUTATION_OPTIONS);
    }

    private static ConfigurationNodeDefinition ResolveTargetNode(
        ConfigurationDefinition definition,
        LogicalPath logicalPath)
    {
        if (ConfigurationSchemaNavigator.ResolveNode(definition.Root, logicalPath) is { } target)
        {
            return target;
        }

        throw new ConfigurationValidationFailedException(
            $"{logicalPath.ToCanonicalString()}: Logical path '{logicalPath}' does not exist in definition '{definition.DefinitionKey}'.");
    }
}
