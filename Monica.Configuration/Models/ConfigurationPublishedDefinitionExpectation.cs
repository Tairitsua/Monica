using Monica.Configuration.Exceptions;

namespace Monica.Configuration.Models;

/// <summary>The active publication reviewed before an existing managed document may be changed.</summary>
/// <remarks>Stores must recheck this evidence and document existence inside the same boundary as the value write.</remarks>
public sealed record ConfigurationPublishedDefinitionExpectation
{
    /// <summary>Gets the locally validated definition, including its published revision and validation contract.</summary>
    public required ConfigurationDefinition Definition { get; init; }

    /// <summary>Rejects missing, retired or changed authority before any persistence occurs.</summary>
    public void Validate(ConfigurationPublishedDefinitionEntry? publication, ConfigurationEffectiveValueDocument? document)
    {
        if (document is null || !string.Equals(document.DefinitionKey, Definition.DefinitionKey, StringComparison.OrdinalIgnoreCase)
            || document.SchemaVersion != Definition.SchemaVersion
            || publication?.IsAvailable != true
            || publication.Metadata.LifecycleState != ConfigurationDefinitionLifecycleState.Active
            || publication.Metadata.DefinitionRevision != Definition.DefinitionRevision
            || !Matches(Definition, publication.Definition!))
            throw new ConfigurationConcurrencyConflictException(
                $"The active publication or existing document for '{Definition.DefinitionKey}' changed before persistence. Review its authority again.");
    }

    /// <summary>Compares the complete portable and executable validation identities without loading published CLR types.</summary>
    public static bool Matches(ConfigurationDefinition expected, ConfigurationDefinition published) =>
        expected.Kind == ConfigurationKind.Managed && published.Kind == ConfigurationKind.Managed
        && string.Equals(expected.DefinitionKey, published.DefinitionKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.SectionPath, published.SectionPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.SchemaHash, published.SchemaHash, StringComparison.Ordinal)
        && expected.SchemaVersion == published.SchemaVersion
        && expected.ValidationContract.Capability != ConfigurationValidationCapability.Unknown
        && !string.IsNullOrWhiteSpace(expected.ValidationContract.Revision)
        && expected.ValidationContract == published.ValidationContract;
}
