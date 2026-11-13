using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Monica.Framework.Generators.ChangeDataGenerator;

/// <summary>
/// Property flattener, flatten the properties of nested objects into a single-layer structure
/// </summary>
internal class PropertyFlattener
{
    /// <summary>
    /// Flattened property list
    /// </summary>
    public FlattenedPropertyResult FlattenProperties(EntityAnalysisResult analysisResult)
    {
        var flattenedProperties = new List<FlattenedProperty>();
        var navigationGroups = new Dictionary<string, NavigationPropertyGroup>();

        foreach (var property in analysisResult.Properties)
        {
            if (property.IsOptionalNavigation)
            {
                // Optional navigation attributes need to be grouped
                ProcessOptionalNavigationProperty(property, navigationGroups, analysisResult.EntitySymbol);
            }
            else
            {
                // Ordinary properties or properties flattened from the Owned type
                var flattenedProperty = CreateFlattenedProperty(property);
                flattenedProperties.Add(flattenedProperty);
            }
        }

        return new FlattenedPropertyResult(
            flattenedProperties.ToImmutableList(),
            navigationGroups.Values.ToImmutableList()
        );
    }

    /// <summary>
    /// Handling optional navigation properties
    /// </summary>
    private void ProcessOptionalNavigationProperty(PropertyInfo property, Dictionary<string, NavigationPropertyGroup> navigationGroups, Microsoft.CodeAnalysis.INamedTypeSymbol entitySymbol)
    {
        // Extract navigation property name from property path
        var pathParts = property.PropertyPath.Split('.');
        var navigationPropertyName = pathParts[0];

        if (!navigationGroups.TryGetValue(navigationPropertyName, out var group))
        {
            group = new NavigationPropertyGroup(
                navigationPropertyName,
                new List<FlattenedProperty>(),
                entitySymbol // 传入根实体符号用于类型解析
            );
            navigationGroups[navigationPropertyName] = group;
        }

        var flattenedProperty = CreateFlattenedProperty(property);
        group.Properties.Add(flattenedProperty);
    }

    /// <summary>
    /// Create flat properties
    /// </summary>
    private FlattenedProperty CreateFlattenedProperty(PropertyInfo property)
    {
        return new FlattenedProperty(
            GetFlattenedPropertyName(property),
            property.Type,
            property.OriginalType,
            property.PropertyPath,
            property.PropertyPath.Contains('.') && !property.IsOptionalNavigation,
            property.IsOptionalNavigation,
            property.PropertySymbol,
            property.XmlDocumentation,
            property.DisplayName
        );
    }

    /// <summary>
    /// Get the flattened attribute name
    /// </summary>
    private string GetFlattenedPropertyName(PropertyInfo property)
    {
        // For nested properties, use the last paragraph as the property name
        // For example: Plan.Callsign -> Callsign
        //      DepInfo.COBT -> COBT
        var pathParts = property.PropertyPath.Split('.');
        return pathParts[pathParts.Length - 1];
    }
}

/// <summary>
/// Flatten attribute results
/// </summary>
internal class FlattenedPropertyResult(
    ImmutableList<FlattenedProperty> properties,
    ImmutableList<NavigationPropertyGroup> navigationGroups)
{
    public ImmutableList<FlattenedProperty> Properties { get; } = properties;
    public ImmutableList<NavigationPropertyGroup> NavigationGroups { get; } = navigationGroups;
}

/// <summary>
/// Flat properties
/// </summary>
internal class FlattenedProperty(
    string name,
    string type,
    string originalType,
    string propertyPath,
    bool isFromOwnedType,
    bool isOptionalNavigation,
    Microsoft.CodeAnalysis.IPropertySymbol originalPropertySymbol,
    string? xmlDocumentation = null,
    string? displayName = null)
{
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string OriginalType { get; } = originalType;
    public string PropertyPath { get; } = propertyPath;
    public string? XmlDocumentation { get; } = xmlDocumentation;
    public string DisplayName { get; } = string.IsNullOrWhiteSpace(displayName) ? name : displayName!;
    public bool IsFromOwnedType { get; } = isFromOwnedType;
    public bool IsOptionalNavigation { get; } = isOptionalNavigation;
    public Microsoft.CodeAnalysis.IPropertySymbol OriginalPropertySymbol { get; } = originalPropertySymbol;
}

/// <summary>
/// Navigation property group
/// </summary>
internal class NavigationPropertyGroup
{
    public NavigationPropertyGroup(string navigationPropertyName, List<FlattenedProperty> properties, Microsoft.CodeAnalysis.INamedTypeSymbol? entitySymbol = null)
    {
        NavigationPropertyName = navigationPropertyName;
        Properties = properties;
        
        // Find navigation properties directly from the root entity type
        var navProperty = entitySymbol?.GetMembers().OfType<Microsoft.CodeAnalysis.IPropertySymbol>()
            .FirstOrDefault(p => p.Name == navigationPropertyName);
                
        if (navProperty != null)
        {
            NavigationPropertyTypeName = GetNavigationPropertyTypeName(navProperty.Type);
        }

        NavigationPropertyTypeName ??= $"Unknown{navigationPropertyName}";
    }
    
    public string NavigationPropertyName { get; }
    public List<FlattenedProperty> Properties { get; }
    public string NavigationPropertyTypeName { get; private set; }
    
    private string GetNavigationPropertyTypeName(Microsoft.CodeAnalysis.ITypeSymbol type)
    {
        // If it is a nullable type, get the underlying type
        if (type is Microsoft.CodeAnalysis.INamedTypeSymbol namedType && 
            namedType.IsGenericType && 
            namedType.OriginalDefinition.SpecialType == Microsoft.CodeAnalysis.SpecialType.System_Nullable_T)
        {
            return namedType.TypeArguments[0].Name;
        }
        
        return type.Name;
    }
}
