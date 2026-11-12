using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Separates local inspection from permission to change an existing published document.</summary>
internal sealed class ConfigurationWriteAuthority(ConfigurationRuntimeContext runtimeContext,
    IServiceProvider services, ConfigurationLocalDefinitionRegistry localDefinitions)
{
    internal bool IsManual => !runtimeContext.AutonomousWork;

    internal async Task<(ConfigurationPublishedDefinitionExpectation? Expectation, string? Reason)> InspectAsync(
        ConfigurationDefinition definition, CancellationToken cancellationToken)
    {
        if (!IsManual || definition.Kind == ConfigurationKind.Startup) return (null, null);
        var metadata = services.GetService<IConfigurationMetadataStore>();
        var values = services.GetService<IConfigurationEffectiveValueStore>();
        if (metadata is null || values is null)
            return (null, "Manual configuration edits require a configured publication and effective-value store.");
        if (!values.SupportsPublishedDefinitionGuard
            || services.GetService<IConfigurationMutationBatchStore>()?.SupportsPublishedDefinitionGuard != true
            || !string.Equals(metadata.Descriptor.StoreKey, values.Descriptor.StoreKey, StringComparison.Ordinal))
            return (null, "This store cannot atomically guard Manual edits against the published definition.");
        var publication = await metadata.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, cancellationToken);
        if (publication?.IsAvailable != true || publication.Metadata.LifecycleState != ConfigurationDefinitionLifecycleState.Active)
            return (null, "Manual configuration edits require an existing active published definition.");
        if (!ConfigurationPublishedDefinitionExpectation.Matches(definition, publication.Definition!))
            return (null, "The local section, schema or validation contract differs from the active publication. Publish through its deployed owner first.");
        if (definition.ValidationContract.Capability == ConfigurationValidationCapability.ObjectCode
            && !localDefinitions.TryGet(definition.DefinitionKey, out _))
            return (null, "The published contract requires matching local executable validation before it can be edited.");
        var document = await values.GetAsync(definition.DefinitionKey, cancellationToken);
        if (document is null)
            return (null, "This value is not persisted. Its deployed owner must create the document before Manual edits are allowed.");
        if (document.SchemaVersion != definition.SchemaVersion)
            return (null, "The stored document schema version differs from the active publication. Migrate it through its deployed owner first.");
        return (new ConfigurationPublishedDefinitionExpectation
        {
            Definition = definition with { DefinitionRevision = publication.Metadata.DefinitionRevision }
        }, null);
    }

    internal async Task<ConfigurationPublishedDefinitionExpectation?> RequireAsync(ConfigurationDefinition definition,
        ConfigurationMutationTarget target, CancellationToken cancellationToken)
    {
        if (!IsManual) return null;
        if (target is not ConfigurationEffectiveStoreMutationTarget)
            throw new ConfigurationValidationFailedException("External JSON sources are read-only in Manual mode. Use the guarded Monica effective-store target.");
        var result = await InspectAsync(definition, cancellationToken);
        if (result.Reason is { } reason) throw new ConfigurationValidationFailedException(reason);
        return result.Expectation;
    }
}
