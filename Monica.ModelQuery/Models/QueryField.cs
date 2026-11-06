using System.Text;
using System.Text.Json.Serialization;
using Monica.Modules;
using Monica.Tool.Extensions;

namespace Monica.ModelQuery.Models;

/// <summary>
/// ModelQuery field metadata.
/// </summary>
public class QueryField
{
    /// <summary>
    /// Names that can be used to reference this field in query expressions.
    /// </summary>
    public HashSet<string> QueryNames { get; set; } = [];

    #region Navigation

    /// <summary>
    /// Navigation-property path segments from the root model to this field,
    /// together with a flag indicating whether each segment is an <c>ICollection</c>.
    /// </summary>
    [JsonIgnore]
    public List<(string RefelectName, bool IsCollection)>? NavigationProperties { get; set; }

    [JsonInclude]
    private string? NavigationPropertiesFormat =>
        NavigationProperties?.Select(p => p.IsCollection ? $"List<{p.RefelectName}>" : p.RefelectName).StringJoin('.');


    #region DynamicLinq Mode 1

    public string GetConditionExpressionParam()
    {
        if (NavigationProperties is null) return ReflectionName;
        var sb = new StringBuilder().Append(ReflectionName);
        for (var i = NavigationProperties.Count - 1; i >= 0; i--)
        {
            var (navigation, isCollection) = NavigationProperties[i];
            if (isCollection) break;
            sb.Insert(0, $"{navigation}.");
        }

        return sb.ToString();
    }

    public string BlendNavigationParam(string conditionExpression)
    {
        if (NavigationProperties is null) return conditionExpression;
        return CreateNavigationExpression();

        string CreateNavigationExpression(string? prefix = null, int navigationIndex = 0)
        {
            var navigationInfo = NavigationProperties.ElementAtOrDefault(navigationIndex++);
            var navigationName = navigationInfo.RefelectName;
            var isCollection = navigationInfo.IsCollection;
            if (navigationInfo == default) // Reached the actual field; no navigation segment remains.
            {
                var fieldName = ReflectionName;
                if ((TypeInfo.TypeFeatures & ETypeFeatures.IsCollection) != 0)
                {

                    return $"{fieldName} != null && {fieldName}.Any({conditionExpression})";
                }

                return conditionExpression;
            }

            var curObj = prefix?.Be($"{prefix}.{navigationName}") ?? navigationName;
            if (isCollection)
            {
                return
                    $"{curObj} != null && {curObj}.Any({CreateNavigationExpression(curObj, navigationIndex)})";
            }

            return CreateNavigationExpression(curObj, navigationIndex);
        }
    }

    #endregion





    #endregion
    /// <summary>
    /// Display name of the field. Defaults to the reflected property name.
    /// </summary>
    public required string Title { get; set; }
    /// <summary>
    /// Reflected property name.
    /// </summary>
    public required string ReflectionName { get; set; }

    /// <summary>
    /// Fuzzy-match configuration.
    /// </summary>
    public required QueryFieldFuzzySearchInfo FuzzySearch { get; set; }

    /// <summary>
    /// Field type metadata.
    /// </summary>
    public required QueryFieldTypeInfo TypeInfo { get; set; }
    /// <summary>
    /// <inheritdoc cref="ModuleModelQueryOption.OmitNavigationPrefix"/>
    /// </summary>
    public bool OmitNavigationPrefix { get; set; }

    /// <summary>
    /// Gets the fallback query name derived from the reflected property and navigation path.
    /// Explicit query names replace this fallback and may leave it unavailable for lookup.
    /// </summary>
    public string DefaultQueryName =>
        OmitNavigationPrefix ? ReflectionName : $"{NavigationProperties?.Select(s => s.RefelectName).StringJoin(".").BeIfNotEmpty("{0}.", true)}{ReflectionName}";
    public override string ToString()
    {
        return $"{NavigationPropertiesFormat?.BeIfNotEmpty("{0}.", true)}{ReflectionName}({string.Join(',', QueryNames)})[{TypeInfo}]";
    }
}
