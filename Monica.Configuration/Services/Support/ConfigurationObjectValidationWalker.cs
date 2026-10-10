using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Executes each schema-owned object's pure synchronous rules independently of attribute failures.</summary>
internal sealed class ConfigurationObjectValidationWalker
{
    internal IReadOnlyList<ConfigurationValueValidationIssue> Validate(ConfigurationLocalDefinitionDescriptor descriptor, object instance)
    {
        var issues = new List<ConfigurationValueValidationIssue>();
        Walk(descriptor.Definition, descriptor.Root, instance, LogicalPath.Root, false,
            new HashSet<object>(ReferenceEqualityComparer.Instance), issues);
        return issues.AsReadOnly();
    }

    private static void Walk(ConfigurationDefinition definition, ConfigurationObjectNodeDescriptor node, object? value,
        LogicalPath path, bool sensitiveAncestor, ISet<object> ancestors, IList<ConfigurationValueValidationIssue> issues)
    {
        if (value is null || node.Schema.NodeKind == ConfigurationNodeKind.Scalar) return;
        if (node.Schema.NodeKind == ConfigurationNodeKind.Object && value.GetType() != (Nullable.GetUnderlyingType(node.Type) ?? node.Type))
            throw Fault(definition, path, "undeclared-object-shape", ConfigurationValidationIssueKind.Contract);
        if (!ancestors.Add(value)) throw Fault(definition, path, "recursive-object-value", ConfigurationValidationIssueKind.Contract);
        var sensitive = sensitiveAncestor || node.Schema.IsSensitive;
        try
        {
            if (value is IValidatableObject validator)
            {
                foreach (var (message, members) in ReadRuleResults(definition, path, value, validator))
                {
                    LogicalPath[] locations = members.Length == 0 ? [path] : members.Select(member =>
                    {
                        if (string.IsNullOrEmpty(member)) return path;
                        var property = node.Properties.FirstOrDefault(property => string.Equals(property.Property.Name, member, StringComparison.Ordinal));
                        if (property is null) throw Fault(definition, path, "validation-member", ConfigurationValidationIssueKind.Contract);
                        return path.Append(new PropertySegment(property.Node.Schema.Name));
                    }).ToArray();
                    var redact = sensitive || HasSensitiveSubtree(node.Schema)
                        || locations.Any(location => ConfigurationSchemaNavigator.IsSensitivePath(definition.Root, location));
                    foreach (var location in locations)
                    {
                        issues.Add(new ConfigurationValueValidationIssue
                        {
                            Kind = ConfigurationValidationIssueKind.Code,
                            LogicalPath = location,
                            LogicalPaths = Array.AsReadOnly(locations),
                            Node = ConfigurationSchemaNavigator.ResolveNode(definition.Root, location) ?? node.Schema,
                            IsSensitive = redact,
                            Message = redact ? "A configuration object rule failed for a sensitive value."
                                : message ?? "A configuration object rule failed."
                        });
                    }
                }
            }

            foreach (var property in node.Properties)
            {
                var childPath = path.Append(new PropertySegment(property.Node.Schema.Name));
                object? child;
                try { child = property.Property.GetValue(value); }
                catch (Exception exception) { throw Fault(definition, childPath, "property-read", inner: exception); }
                Walk(definition, property.Node, child, childPath, sensitive, ancestors, issues);
            }
            if (node.DictionaryValue is { } dictionary)
                foreach (var (key, item) in ConfigurationObjectMaterializer.ReadDictionaryEntries(definition, value, path, "object-traversal"))
                    Walk(definition, dictionary, item, path.Append(new DictionaryKeySegment(key)), sensitive, ancestors, issues);
            if (node.ListItem is { } list && value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in ConfigurationObjectMaterializer.ReadListItems(definition, enumerable, path, "object-traversal"))
                {
                    var itemPath = path.Append(new ListIndexSegment(index++));
                    if (item is not null && node.Schema.ListTemplate?.ItemKeyPropertyName is { } alias)
                    {
                        var keyProperty = list.Properties.FirstOrDefault(property => string.Equals(property.Node.Schema.Name, alias, StringComparison.OrdinalIgnoreCase));
                        string? key;
                        try
                        {
                            var keyValue = keyProperty?.Property.GetValue(item);
                            key = keyValue is null ? null : Convert.ToString(keyValue, CultureInfo.InvariantCulture);
                        }
                        catch (Exception exception) { throw Fault(definition, itemPath, "property-read", inner: exception); }
                        if (key is not null) itemPath = path.Append(new ListItemKeySegment(key));
                    }
                    Walk(definition, list, item, itemPath, sensitive, ancestors, issues);
                }
            }
        }
        catch (ConfigurationValidationExecutionException) { throw; }
        catch (Exception exception) { throw Fault(definition, path, "object-traversal", inner: exception); }
        finally { ancestors.Remove(value); }
    }

    private static (string? Message, string[] Members)[] ReadRuleResults(ConfigurationDefinition definition,
        LogicalPath path, object value, IValidatableObject validator)
    {
        try
        {
            var captured = new List<(string? Message, string[] Members)>();
            var results = validator.Validate(new ValidationContext(value));
            if (results is not null)
                foreach (var result in results)
                    if (result is not null) captured.Add((result.ErrorMessage, result.MemberNames?.ToArray() ?? []));
            return captured.ToArray();
        }
        // Even the public framework fault type is untrusted when it originates in user code.
        catch (Exception exception) { throw Fault(definition, path, "object-rule", inner: exception); }
    }

    private static bool HasSensitiveSubtree(ConfigurationNodeDefinition node) => node.IsSensitive
        || node.Children.Any(HasSensitiveSubtree)
        || node.ListTemplate is { } list && HasSensitiveSubtree(list.ItemTemplate)
        || node.DictionaryTemplate is { } dictionary && HasSensitiveSubtree(dictionary.ValueTemplate);

    private static ConfigurationValidationExecutionException Fault(ConfigurationDefinition definition, LogicalPath path,
        string stage, ConfigurationValidationIssueKind kind = ConfigurationValidationIssueKind.Execution, Exception? inner = null)
        => new(definition.DefinitionKey, path, stage, kind, inner);
}
