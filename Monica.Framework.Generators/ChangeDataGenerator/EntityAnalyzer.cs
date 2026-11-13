using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Monica.Framework.Generators.ChangeDataGenerator;

/// <summary>
/// Entity analyzer, used to analyze entity classes and their properties
/// </summary>
internal class EntityAnalyzer(Compilation compilation, CancellationToken cancellationToken)
{
    private static readonly string[] IgnoredPropertyNames = ["Id", "ExtraProperties", "ConcurrencyStamp"];

    private readonly INamedTypeSymbol? _ownedAttributeSymbol = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.OwnedAttribute");
    private readonly INamedTypeSymbol? _changeItemPropertyAttributeSymbol = compilation.GetTypeByMetadataName("Monica.Framework.ChangeTracking.Annotations.ChangeEntryPropertyAttribute");
    private readonly INamedTypeSymbol? _notMappedAttributeSymbol = compilation.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute");
    private readonly INamedTypeSymbol? _jsonIgnoreAttributeSymbol = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonIgnoreAttribute");
    private readonly INamedTypeSymbol? _dateTimeSymbol = compilation.GetTypeByMetadataName("System.DateTime");
    private readonly INamedTypeSymbol? _dateTimeOffsetSymbol = compilation.GetTypeByMetadataName("System.DateTimeOffset");
    private readonly INamedTypeSymbol? _timeSpanSymbol = compilation.GetTypeByMetadataName("System.TimeSpan");
    private readonly INamedTypeSymbol? _guidSymbol = compilation.GetTypeByMetadataName("System.Guid");
    private readonly INamedTypeSymbol? _listSymbol = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
    private readonly INamedTypeSymbol? _iListSymbol = compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1");

    /// <summary>
    /// Analyze entity classes and extract all attribute information that needs to be generated
    /// </summary>
    public EntityAnalysisResult? AnalyzeEntity(INamedTypeSymbol entitySymbol)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var properties = new List<PropertyInfo>();
            var ownedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            // Analyze all public properties of an entity
            AnalyzePropertiesRecursive(entitySymbol, properties, ownedTypes, "", false);

            return new EntityAnalysisResult(
                entitySymbol,
                properties.ToImmutableList(),
                ownedTypes.ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively parse properties, handle nested Owned types and optional navigation properties
    /// </summary>
    private void AnalyzePropertiesRecursive(
        INamedTypeSymbol typeSymbol,
        List<PropertyInfo> properties,
        HashSet<INamedTypeSymbol> ownedTypes,
        string propertyPathPrefix,
        bool isFromOptionalNavigation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var publicProperties = typeSymbol
            .GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public)
            .Where(p => !p.IsStatic)
            .Where(p => p.SetMethod != null) // 必须有 setter
            .Where(p => !IsIgnoredProperty(p))
            .ToList();

        foreach (var property in publicProperties)
        {
            var propertyPath = string.IsNullOrEmpty(propertyPathPrefix) 
                ? property.Name 
                : $"{propertyPathPrefix}.{property.Name}";

            // Check if it is of type Owned
            if (IsOwnedType(property.Type))
            {
                if (property.Type is INamedTypeSymbol namedType)
                {
                    ownedTypes.Add(namedType);
                    
                    // Recursively analyze properties of Owned type and flatten them
                    AnalyzePropertiesRecursive(namedType, properties, ownedTypes, propertyPath, isFromOptionalNavigation);
                }
            }
            // Check if optional navigation properties (such as DepInfo, ArrInfo)
            else if (IsOptionalNavigationProperty(property))
            {
                var underlyingType = GetUnderlyingType(property.Type);
                if (underlyingType is INamedTypeSymbol namedType)
                {
                    // Recursively analyze properties of optional navigation properties
                    AnalyzePropertiesRecursive(namedType, properties, ownedTypes, propertyPath, true);
                }
            }
            else
            {
                // Common properties
                var propertyInfo = CreatePropertyInfo(property, propertyPath, isFromOptionalNavigation);
                if (propertyInfo != null)
                {
                    properties.Add(propertyInfo);
                }
            }
        }
    }

    /// <summary>
    /// Create attribute information
    /// </summary>
    private PropertyInfo? CreatePropertyInfo(
        IPropertySymbol property,
        string propertyPath,
        bool isFromOptionalNavigation = false)
    {
        try
        {
            var xmlDoc = ExtractXmlDocumentation(property);
            var isNullable = IsNullableType(property.Type);
            var propertyType = GetPropertyType(property.Type);
            var isOptionalNavigation = isFromOptionalNavigation || IsOptionalNavigationProperty(property);
            var displayName = GetPropertyDisplayName(property);

            return new PropertyInfo(
                property.Name,
                propertyPath,
                propertyType,
                property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                isNullable,
                isOptionalNavigation,
                property,
                xmlDoc,
                displayName
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract XML document comments
    /// </summary>
    private string? ExtractXmlDocumentation(IPropertySymbol property)
    {
        // Using XML document comments
        var xmlDoc = property.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xmlDoc))
            return null;

        try
        {
            var summaryStart = xmlDoc!.IndexOf("<summary>");
            var summaryEnd = xmlDoc.IndexOf("</summary>");
            
            if (summaryStart >= 0 && summaryEnd > summaryStart)
            {
                var summary = xmlDoc.Substring(summaryStart + 9, summaryEnd - summaryStart - 9).Trim();
                return string.IsNullOrWhiteSpace(summary) ? null : summary;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    /// <summary>
    /// Check if it is of type Owned
    /// </summary>
    private bool IsOwnedType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType)
            return false;

        return HasAttribute(namedType.GetAttributes(), _ownedAttributeSymbol);
    }

    /// <summary>
    /// Check if optional navigation attribute
    /// </summary>
    private bool IsOptionalNavigationProperty(IPropertySymbol property)
    {
        // If the type is a nullable complex type and is not Owned, it is considered an optional navigation property.
        if (!IsNullableType(property.Type))
            return false;

        var underlyingType = GetUnderlyingType(property.Type);
        return underlyingType is INamedTypeSymbol namedType && 
               !IsBuiltInType(namedType) && 
               !IsOwnedType(namedType);
    }

    /// <summary>
    /// Check if it is a built-in type
    /// </summary>
    private bool IsBuiltInType(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum || type.SpecialType != SpecialType.None)
            return true;

        if (SymbolEquals(type, _dateTimeSymbol) ||
            SymbolEquals(type, _dateTimeOffsetSymbol) ||
            SymbolEquals(type, _timeSpanSymbol) ||
            SymbolEquals(type, _guidSymbol))
        {
            return true;
        }

        if (!type.IsGenericType)
            return false;

        return SymbolEquals(type.OriginalDefinition, _listSymbol) ||
               SymbolEquals(type.OriginalDefinition, _iListSymbol);
    }

    /// <summary>
    /// Check if it is a nullable type
    /// </summary>
    private bool IsNullableType(ITypeSymbol type)
    {
        return type.IsReferenceType || IsNullableValueType(type);
    }

    /// <summary>
    /// Get attribute type string
    /// </summary>
    private string GetPropertyType(ITypeSymbol type)
    {
        // If it is already a nullable type, return directly
        if (IsNullableType(type))
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        // For value types, add the nullable modifier
        if (type.IsValueType)
        {
            return $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}?";
        }

        // Reference type returns nullable version
        return $"{type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}?";
    }

    /// <summary>
    /// Get the underlying type (if it is a nullable type)
    /// </summary>
    private ITypeSymbol GetUnderlyingType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol namedType && IsNullableValueType(namedType))
        {
            return namedType.TypeArguments[0];
        }

        return type;
    }

    /// <summary>
    /// Check if a property should be ignored
    /// </summary>
    private bool IsIgnoredProperty(IPropertySymbol property)
    {
        // Ignore indexer
        if (property.IsIndexer)
            return true;

        // Check the Ignore setting of the change-item property attribute.
        var alterItemAttr = GetChangeItemPropertyAttribute(property);
        
        if (alterItemAttr != null)
        {
            var ignoreValue = GetAttributeArgumentValue<bool>(alterItemAttr, "Ignore");
            if (ignoreValue)
                return true;
        }

        // Ignore properties with the [NotMapped] attribute
        var attributes = property.GetAttributes();
        if (HasAttribute(attributes, _notMappedAttributeSymbol))
            return true;

        // Ignore properties with [JsonIgnore] attribute
        if (HasAttribute(attributes, _jsonIgnoreAttributeSymbol))
            return true;

        // Ignore common base class properties
        if (IgnoredPropertyNames.Contains(property.Name))
            return true;

        return false;
    }

    private bool IsNullableValueType(ITypeSymbol type)
    {
        return type is INamedTypeSymbol namedType &&
               namedType.IsGenericType &&
               namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    private AttributeData? GetChangeItemPropertyAttribute(IPropertySymbol property)
    {
        return _changeItemPropertyAttributeSymbol == null
            ? null
            : property.GetAttributes()
                .FirstOrDefault(attr => SymbolEquals(attr.AttributeClass, _changeItemPropertyAttributeSymbol));
    }

    private string GetPropertyDisplayName(IPropertySymbol property)
    {
        var changeItemPropertyAttribute = GetChangeItemPropertyAttribute(property);
        if (changeItemPropertyAttribute == null)
            return property.Name;

        var titleNamedArg = changeItemPropertyAttribute.NamedArguments
            .FirstOrDefault(arg => arg.Key == "Title");

        return !titleNamedArg.Equals(default(KeyValuePair<string, TypedConstant>)) &&
               titleNamedArg.Value.Value is string titleValue &&
               !string.IsNullOrEmpty(titleValue)
            ? titleValue
            : property.Name;
    }

    private static bool HasAttribute(ImmutableArray<AttributeData> attributes, INamedTypeSymbol? targetSymbol)
    {
        return targetSymbol != null && attributes.Any(attr => SymbolEquals(attr.AttributeClass, targetSymbol));
    }

    private static bool SymbolEquals(ISymbol? left, ISymbol? right)
    {
        return left != null && right != null && SymbolEqualityComparer.Default.Equals(left, right);
    }
    
    /// <summary>
    /// Get attribute parameter value
    /// </summary>
    private static T GetAttributeArgumentValue<T>(AttributeData attribute, string parameterName)
    {
        // Find named parameters
        var namedArg = attribute.NamedArguments
            .FirstOrDefault(kvp => kvp.Key == parameterName);

        if (namedArg.Value.IsNull || namedArg.Value.Value == null)
            return default(T)!;

        return (T)namedArg.Value.Value;
    }
}

/// <summary>
/// Entity analysis results
/// </summary>
internal class EntityAnalysisResult(
    INamedTypeSymbol entitySymbol,
    ImmutableList<PropertyInfo> properties,
    ImmutableHashSet<INamedTypeSymbol> ownedTypes)
{
    public INamedTypeSymbol EntitySymbol { get; } = entitySymbol;
    public ImmutableList<PropertyInfo> Properties { get; } = properties;
    public ImmutableHashSet<INamedTypeSymbol> OwnedTypes { get; } = ownedTypes;
}

/// <summary>
/// Attribute information
/// </summary>
internal class PropertyInfo(
    string name,
    string propertyPath,
    string type,
    string originalType,
    bool isNullable,
    bool isOptionalNavigation,
    IPropertySymbol propertySymbol,
    string? xmlDocumentation = null,
    string? displayName = null)
{
    public string Name { get; } = name;
    public string PropertyPath { get; } = propertyPath;
    public string Type { get; } = type;
    public string OriginalType { get; } = originalType;
    public bool IsNullable { get; } = isNullable;
    public bool IsOptionalNavigation { get; } = isOptionalNavigation;
    public string? XmlDocumentation { get; } = xmlDocumentation;
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? name : displayName!;
    public IPropertySymbol PropertySymbol { get; } = propertySymbol;
}
