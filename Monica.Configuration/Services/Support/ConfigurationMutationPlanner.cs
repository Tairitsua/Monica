using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Utils;

namespace Monica.Configuration.Services.Support;

internal sealed class ConfigurationMutationPlanner(
    ConfigurationDefinitionResolver definitionResolver,
    ConfigurationValidationCoordinator validationCoordinator,
    ConfigurationPathProjector pathProjector,
    IConfigurationSourceInspector sourceInspector)
{
    public async Task<PreparedConfigurationMutation> PrepareAsync(
        ConfigurationMutationCommand command,
        ConfigurationMutationContext context,
        CancellationToken cancellationToken)
    {
        var definition = await definitionResolver.GetRequiredAsync(command.DefinitionKey, cancellationToken);
        ValidateSourceChainRevision(definition, command);
        var targetNode = ResolveTargetNode(definition, command.LogicalPath);
        var request = new ConfigurationMutationRequest
        {
            DefinitionKey = command.DefinitionKey,
            LogicalPath = command.LogicalPath,
            MutationKind = command.MutationKind,
            Value = command.MutationKind == ConfigurationMutationKind.Set
                ? ConfigurationRegexTextCodec.NormalizeStoredValue(targetNode, command.Value)
                : command.Value,
            ExpectedSchemaVersion = command.ExpectedSchemaVersion,
            ExpectedSchemaHash = command.ExpectedSchemaHash,
            ExpectedValueVersion = command.Target is ConfigurationEffectiveStoreMutationTarget target
                ? target.ExpectedVersion
                : null,
            Context = context
        };
        validationCoordinator.Validate(definition, request, validateFragment: false);
        ValidateEditablePath(definition, command.LogicalPath);

        return new PreparedConfigurationMutation
        {
            Command = command,
            Definition = definition,
            TargetNode = targetNode,
            Request = request,
            ConfigurationPath = pathProjector.Project(definition.SectionPath, command.LogicalPath),
            Granularity = targetNode.NodeKind == ConfigurationNodeKind.Scalar
                ? ConfigurationMutationGranularity.Scalar
                : ConfigurationMutationGranularity.Container
        };
    }

    private void ValidateSourceChainRevision(
        ConfigurationDefinition definition,
        ConfigurationMutationCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ExpectedSourceChainRevision))
        {
            return;
        }

        var currentRevision = ConfigurationSourceChainRevision.Compute(
            sourceInspector.GetSourceChain(definition, command.LogicalPath));
        if (!string.Equals(
                currentRevision,
                command.ExpectedSourceChainRevision,
                StringComparison.Ordinal))
        {
            throw new ConfigurationConcurrencyConflictException(
                $"The effective source chain for '{command.DefinitionKey}' at '{command.LogicalPath}' changed after review.");
        }
    }

    private static ConfigurationNodeDefinition ResolveTargetNode(
        ConfigurationDefinition definition,
        LogicalPath logicalPath)
    {
        var current = definition.Root;
        foreach (var segment in logicalPath.Segments)
        {
            current = ResolveChild(current, segment)
                ?? throw new ConfigurationValidationFailedException(
                    $"Logical path '{logicalPath}' does not exist in definition '{definition.DefinitionKey}'.");
        }

        return current;
    }

    private static void ValidateEditablePath(ConfigurationDefinition definition, LogicalPath logicalPath)
    {
        var current = definition.Root;
        foreach (var segment in logicalPath.Segments)
        {
            switch (segment)
            {
                case PropertySegment property:
                    current = current.Children.FirstOrDefault(child =>
                            string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ConfigurationValidationFailedException(
                            $"Property '{property.Name}' does not exist in definition '{definition.DefinitionKey}'.");
                    break;
                case DictionaryKeySegment dictionaryKey:
                    if (current.DictionaryTemplate is null)
                    {
                        throw new ConfigurationValidationFailedException(
                            $"Path segment '{dictionaryKey.Key}' targets a non-dictionary node in definition '{definition.DefinitionKey}'.");
                    }

                    ConfigurationDictionaryKeyEscaper.ThrowIfInvalidForProjection(dictionaryKey.Key);
                    current = current.DictionaryTemplate.ValueTemplate;
                    break;
                case ListItemKeySegment itemKey:
                    if (current.ListTemplate is not { SupportsPerItemMutation: true } listTemplate)
                    {
                        throw new ConfigurationValidationFailedException(
                            $"List path segment '{itemKey.ItemKey}' requires a list node with a stable item key in definition '{definition.DefinitionKey}'.");
                    }

                    ConfigurationDictionaryKeyEscaper.ThrowIfInvalidForProjection(itemKey.ItemKey);
                    current = listTemplate.ItemTemplate;
                    break;
                case ListIndexSegment:
                    throw new ConfigurationValidationFailedException(
                        "ListIndexSegment is projection-only and cannot be used in mutation requests. Use ListItemKeySegment for per-item list mutations.");
                default:
                    throw new ConfigurationValidationFailedException(
                        $"Unsupported logical path segment '{segment.GetType().Name}'.");
            }
        }
    }

    private static ConfigurationNodeDefinition? ResolveChild(
        ConfigurationNodeDefinition current,
        ConfigurationPathSegment segment)
    {
        return segment switch
        {
            PropertySegment property => current.Children.FirstOrDefault(child =>
                string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase)),
            DictionaryKeySegment => current.DictionaryTemplate?.ValueTemplate,
            ListItemKeySegment or ListIndexSegment => current.ListTemplate?.ItemTemplate,
            _ => null
        };
    }
}

internal sealed record PreparedConfigurationMutation
{
    public required ConfigurationMutationCommand Command { get; init; }

    public required ConfigurationDefinition Definition { get; init; }

    public required ConfigurationNodeDefinition TargetNode { get; init; }

    public required ConfigurationMutationRequest Request { get; init; }

    public required string ConfigurationPath { get; init; }

    public ConfigurationMutationGranularity Granularity { get; init; }
}
