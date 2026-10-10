using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Owns executable descriptors obtained exclusively from successful local discovery.</summary>
internal sealed class ConfigurationLocalDefinitionRegistry
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ConfigurationLocalDefinitionDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryGet(string definitionKey, out ConfigurationLocalDefinitionDescriptor descriptor)
    {
        lock (_lock)
        {
            return _descriptors.TryGetValue(definitionKey, out descriptor!);
        }
    }

    internal ConfigurationLocalDefinitionDescriptor GetRequired(ConfigurationDefinition definition)
    {
        if (TryGet(definition.DefinitionKey, out var descriptor))
        {
            if (definition.Origin != ConfigurationDefinitionOrigin.LocalScan
                || !string.Equals(definition.SchemaHash, descriptor.Definition.SchemaHash, StringComparison.Ordinal)
                || !string.Equals(definition.SectionPath, descriptor.Definition.SectionPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(definition.ValidationContract.Revision, descriptor.Definition.ValidationContract.Revision, StringComparison.Ordinal)
                || definition.ValidationContract.Capability != descriptor.Definition.ValidationContract.Capability)
                throw new ConfigurationValidationExecutionException(definition.DefinitionKey, LogicalPath.Root,
                    "local-validation-contract", ConfigurationValidationIssueKind.Contract);
            return descriptor;
        }

        throw new ConfigurationValidationExecutionException(
            definition.DefinitionKey, LogicalPath.Root, "local-descriptor", ConfigurationValidationIssueKind.Contract);
    }

    internal ConfigurationLocalDefinitionDescriptor GetRequired(Type optionsType)
    {
        lock (_lock)
        {
            if (_descriptors.Values.FirstOrDefault(descriptor => descriptor.OptionsType == optionsType) is { } descriptor)
                return descriptor;
        }

        throw new ConfigurationValidationExecutionException(string.Empty, LogicalPath.Root,
            "local-options-owner", ConfigurationValidationIssueKind.Contract);
    }

    internal void RegisterRange(IEnumerable<ConfigurationDefinitionRegistration> registrations)
    {
        var descriptors = registrations.Select(registration => ConfigurationLocalDefinitionDescriptor.Create(
            registration.OptionsType, registration.Definition)).ToArray();
        lock (_lock)
        {
            foreach (var descriptor in descriptors)
            {
                _descriptors[descriptor.Definition.DefinitionKey] = descriptor;
            }
        }
    }
}

/// <summary>Couples a locally discovered schema to its finite, executable CLR property graph.</summary>
internal sealed record ConfigurationLocalDefinitionDescriptor(
    Type OptionsType,
    ConfigurationDefinition Definition,
    ConfigurationObjectNodeDescriptor Root)
{
    internal static ConfigurationLocalDefinitionDescriptor Create(Type type, ConfigurationDefinition definition)
    {
        EnsureRootConstructible(type, definition.DefinitionKey);
        var contract = CreateContract(type, definition.Root);
        if (contract != definition.ValidationContract)
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, LogicalPath.Root,
                "local-discovery-contract", ConfigurationValidationIssueKind.Contract);
        return new(type, definition, Build(type, definition.Root));
    }

    internal static void EnsureRootConstructible(Type type, string definitionKey)
    {
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters
            || type.GetConstructor(Type.EmptyTypes) is null)
            throw new ConfigurationValidationExecutionException(definitionKey, LogicalPath.Root,
                "options-constructor", ConfigurationValidationIssueKind.Contract);
    }

    internal static ConfigurationValidationContract CreateContract(Type type, ConfigurationNodeDefinition schema)
    {
        var root = Build(type, schema);
        var nodes = Enumerate(root).ToArray();
        var code = nodes.Any(node => typeof(IValidatableObject).IsAssignableFrom(node.Type));
        var revisionInput = string.Join("\n", nodes.Select(node =>
                $"{node.Type.AssemblyQualifiedName}|{node.Type.Module.ModuleVersionId:D}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return new ConfigurationValidationContract
        {
            Capability = code ? ConfigurationValidationCapability.ObjectCode : ConfigurationValidationCapability.PortableOnly,
            Revision = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput))).ToLowerInvariant()}"
        };
    }

    private static ConfigurationObjectNodeDescriptor Build(Type type, ConfigurationNodeDefinition schema)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var properties = schema.NodeKind == ConfigurationNodeKind.Object
            ? type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
                .Select(property => (Property: property, Alias: property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? property.Name))
                .Select(item => (item.Property, Schema: schema.Children.Single(child =>
                    string.Equals(child.Name, item.Alias, StringComparison.OrdinalIgnoreCase))))
                .Select(item => new ConfigurationObjectPropertyDescriptor(item.Property, Build(item.Property.PropertyType, item.Schema)))
                .ToArray()
            : [];
        var dictionary = FindGeneric(type, typeof(IDictionary<,>));
        var enumerable = FindGeneric(type, typeof(IEnumerable<>));
        var itemType = type.IsArray ? type.GetElementType() : enumerable?.GetGenericArguments()[0];
        return new ConfigurationObjectNodeDescriptor(
            type,
            schema,
            properties,
            schema.ListTemplate is { } list && itemType is not null ? Build(itemType, list.ItemTemplate) : null,
            schema.DictionaryTemplate is { } map && dictionary is not null
                ? Build(dictionary.GetGenericArguments()[1], map.ValueTemplate) : null,
            dictionary?.GetGenericArguments()[0]);
    }

    internal static Type? FindGeneric(Type type, Type generic)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == generic
            ? type
            : type.GetInterfaces().FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == generic);
    }

    private static IEnumerable<ConfigurationObjectNodeDescriptor> Enumerate(ConfigurationObjectNodeDescriptor node)
    {
        yield return node;
        foreach (var property in node.Properties)
        {
            foreach (var child in Enumerate(property.Node)) yield return child;
        }
        foreach (var template in new[] { node.ListItem, node.DictionaryValue }.OfType<ConfigurationObjectNodeDescriptor>())
        {
            foreach (var child in Enumerate(template)) yield return child;
        }
    }
}

internal sealed record ConfigurationObjectNodeDescriptor(
    Type Type,
    ConfigurationNodeDefinition Schema,
    IReadOnlyList<ConfigurationObjectPropertyDescriptor> Properties,
    ConfigurationObjectNodeDescriptor? ListItem,
    ConfigurationObjectNodeDescriptor? DictionaryValue,
    Type? DictionaryKeyType);

internal sealed record ConfigurationObjectPropertyDescriptor(PropertyInfo Property, ConfigurationObjectNodeDescriptor Node);
