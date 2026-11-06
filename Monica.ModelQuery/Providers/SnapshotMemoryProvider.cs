using System.Collections;
using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Annotations;
using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.ModelQuery.Providers;

/// <summary>
/// Builds and caches in-memory ModelQuery snapshots.
/// </summary>
/// <typeparam name="TModel">The model type.</typeparam>
internal sealed class SnapshotMemoryProvider<TModel> : IModelQuerySnapshot<TModel>
{
    private readonly ModelQuerySnapshot _snapshot;
    private readonly FrozenDictionary<string, QueryField> _fieldDictionary;
    private readonly IReadOnlyList<string> _allQueryNames;

    public SnapshotMemoryProvider(
        IOptions<ModuleModelQueryOption> options,
        IOptions<ModelQueryExpressionOptions> expressionOptions,
        SnapshotFactoryMemoryProvider snapshotFactory)
    {
        (_snapshot, _fieldDictionary, _allQueryNames) = BuildSnapshot(options.Value, expressionOptions.Value);
        snapshotFactory.Register<TModel>(_snapshot);
    }

    private static IEnumerable<PropertyInfo> GetQueryFieldTypes(Type type)
    {
        // Note: string is also a reference type. string? only adds NullableContextAttribute, so treat string? the same as string by checking typeof(string).
        return type.GetProperties().OrderByDescending(p => p.PropertyType == typeof(string)).ThenBy(p => p.PropertyType.IsClass);
    }

    private static (ModelQuerySnapshot Snapshot, FrozenDictionary<string, QueryField> Fields, IReadOnlyList<string> QueryNames)
        BuildSnapshot(ModuleModelQueryOption options, ModelQueryExpressionOptions expressionOptions)
    {
        Dictionary<string, QueryField> fieldDictionary = new(StringComparer.OrdinalIgnoreCase);
        var model = new QueryModel()
        {
            FullTypeName = typeof(TModel).FullName ?? throw new InvalidOperationException(),
            Name = typeof(TModel).Name
        };
        var snapshot = new ModelQuerySnapshot
        {
            Model = model,
            Fields = []
        };
        var requireFieldAnnotation = options.RequireFieldAnnotation;
        var modelAttribute = typeof(TModel).GetCustomAttribute<QueryModelAttribute>();
        if (modelAttribute != null)
        {
            snapshot.Model.Name = modelAttribute.Name ?? snapshot.Model.Name;
            requireFieldAnnotation = modelAttribute.RequireFieldAnnotationOverride ?? requireFieldAnnotation;
        }

        var navigatedTypeNames = new HashSet<Type>(); // Prevent recursive navigation from causing stack overflows. Only one nested level is supported for the same type.
        List<string> allOriginQueryNames = [];

        ExtractFieldInfo(GetQueryFieldTypes(typeof(TModel)));
        return (snapshot, fieldDictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), allOriginQueryNames.ToArray());

        void ExtractFieldInfo(IEnumerable<PropertyInfo> propertyInfos, PropertyInfo? fromNavigateProperty = null, List<(string, bool)>? previousNavigateTuples = null)
        {

            if (fromNavigateProperty?.Name is { } navigationPropertyName)
            {
                previousNavigateTuples ??= [];
                var isICollection = fromNavigateProperty?.PropertyType.IsAssignableTo(typeof(IEnumerable)) ?? false;
                previousNavigateTuples.Add((navigationPropertyName, isICollection));
            }


            foreach (var p in propertyInfos)
            {
                var fieldAttribute = p.GetCustomAttribute<QueryFieldAttribute>();
                if (requireFieldAnnotation && fieldAttribute == null) continue;
                if (fieldAttribute?.Exclude is true) continue;
                // Automatically ignore fields without QueryField when they are annotated with NotMapped or JsonIgnore.
                if (fieldAttribute is null)
                {
                    if (!options.IncludeNotMappedProperties && p.GetCustomAttribute<NotMappedAttribute>() != null) continue;
                    if (!options.IncludeJsonIgnoredProperties && p.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                }


                // Dictionary types are not supported.
                if (typeof(IDictionary).IsAssignableFrom(p.PropertyType)) continue;

                // Determine whether the property should be treated as a navigation property.
                if (p.PropertyType.GetGenericUnderlyingType() is { IsClass: true } underlyingType && underlyingType != typeof(string))
                {
                    if (!navigatedTypeNames.Add(underlyingType)) continue;
                    ExtractFieldInfo(GetQueryFieldTypes(underlyingType), p, previousNavigateTuples?.ToList());
                    continue;
                }

                var queryNames = new List<string>();
                try
                {
                    var field = new QueryField
                    {
                        OmitNavigationPrefix = fieldAttribute?.OmitNavigationPrefixOverride ?? modelAttribute?.OmitNavigationPrefixOverride ?? options.OmitNavigationPrefix,
                        ReflectionName = p.Name,
                        Title = fieldAttribute?.Title ?? p.Name,
                        TypeInfo = new QueryFieldTypeInfo(p.PropertyType, p.DeclaringType),
                        FuzzySearch = new QueryFieldFuzzySearchInfo(),
                        NavigationProperties = previousNavigateTuples
                    };
                    if (fromNavigateProperty != null)
                    {
                        // Keep the navigation path for nested identifiers, even when prefixes are omitted.
                        if (p.Name.Equals("id", StringComparison.OrdinalIgnoreCase))
                        {
                            field.OmitNavigationPrefix = false;
                        }
                        else if (options.AutoQualifyConflictingNames && fieldDictionary.ContainsKey(p.Name))
                        {
                            field.OmitNavigationPrefix = false;
                        }
                    }


                    CheckFuzzySearch(field);

                    if (fieldAttribute != null)
                    {
                        if (fieldAttribute.QueryNames != null)
                        {
                            queryNames.AddRange(fieldAttribute.QueryNames);
                        }

                        field.FuzzySearch.IsIgnored = fieldAttribute.ExcludeFromDefaultFuzzySearch;
                    }


                    if (queryNames.Count == 0)
                    {
                        var propertyName = field.DefaultQueryName;
                        queryNames.Add(propertyName);
                    }

                    if (queryNames.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new ModelQuerySnapshotException(
                            displayMessage: "ModelQuery field query names cannot be blank.",
                            technicalDetail: $"Type: {model.FullTypeName}; field: {field.ReflectionName}.");
                    }

                    var invalidName = queryNames.FirstOrDefault(name =>
                        name.Any(char.IsWhiteSpace) || name.Contains('(') || name.Contains(')') ||
                        (!string.IsNullOrEmpty(expressionOptions.SelectSeparator) &&
                         name.Contains(expressionOptions.SelectSeparator, StringComparison.Ordinal)));
                    if (invalidName != null)
                    {
                        throw new ModelQuerySnapshotException(
                            displayMessage: "ModelQuery field query names cannot contain whitespace, parentheses, or the selection separator.",
                            technicalDetail: $"Type: {model.FullTypeName}; field: {field.ReflectionName}; query name: {invalidName}.");
                    }

                    field.QueryNames = new HashSet<string>(queryNames, StringComparer.OrdinalIgnoreCase);
                    snapshot.Fields.Add(field);
                    foreach (var name in queryNames)
                    {
                        if (!fieldDictionary.TryAdd(name, field))
                            throw new ModelQuerySnapshotException(
                                displayMessage: "ModelQuery field query names must be unique, ignoring case. Rename the field with QueryFieldAttribute.",
                                technicalDetail: $"Type: {model.FullTypeName}; conflicting field: {field.ReflectionName}; query name: {name}; existing field: {fieldDictionary[name].ReflectionName}.");
                        allOriginQueryNames.Add(name);
                    }
                }
                catch (ModelQueryUnsupportedFieldTypeException)
                {
                    var declaringTypeName = p.DeclaringType?.GetCleanFullName() ?? "Unknown";
                    var propertyTypeName = p.PropertyType.GetCleanFullName();
                    var message = $"ModelQuery ignored unsupported field type {propertyTypeName} on {declaringTypeName}.";
                    if (options.ThrowOnUnsupportedFieldType)
                    {
                        throw new ModelQuerySnapshotException(
                            displayMessage: "The model contains an unsupported field type.",
                            technicalDetail: $"Type: {propertyTypeName}; declaring type: {declaringTypeName}.");
                    }

                    Console.WriteLine(message); // ModelQuery can be constructed before the application logger is available.
                }
            }
        }
    }

    /// <summary>
    /// Validates fuzzy-match support for a field.
    /// </summary>
    /// <param name="field">The field metadata to update.</param>
    private static void CheckFuzzySearch(QueryField field)
    {
        if (field.TypeInfo.TypeFeatures.HasAnyFlag(ETypeFeatures.IsCollection, ETypeFeatures.IsClass))
        {
            field.FuzzySearch.IsNotSupported = true;
        }
    }

    public IReadOnlyList<string> GetAllQueryNames()
    {
        return _allQueryNames;
    }

    public QueryField? GetField(string fieldQueryName)
    {
        return _fieldDictionary.GetValueOrDefault(fieldQueryName);
    }

    public IReadOnlyList<QueryField> GetFields(IReadOnlyList<string>? fieldQueryNames = null)
    {
        if (fieldQueryNames is null) return _snapshot.Fields;
        var list = new List<QueryField>();
        foreach (var name in fieldQueryNames)
        {
            if (_fieldDictionary.TryGetValue(name, out var field))
            {
                list.Add(field);
            }
        }
        return list;
    }
}
