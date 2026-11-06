namespace Monica.ModelQuery.Models;

/// <summary>
/// Fuzzy-match settings for a field.
/// </summary>
public class QueryFieldFuzzySearchInfo
{
    /// <summary>
    /// Indicates that fuzzy matching is not supported for this field type.
    /// </summary>
    public bool IsNotSupported { get; set; }
    /// <summary>
    /// Indicates that this field is excluded from full-field fuzzy searches.
    /// </summary>
    public bool IsIgnored { get; set; }
}
