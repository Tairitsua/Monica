namespace Monica.ModelQuery.Models;

/// <summary>
/// Search settings for a field.
/// </summary>
public sealed class QueryFieldSearchInfo
{
    internal QueryFieldSearchInfo(bool isNotSupported, bool isIgnored)
    {
        IsNotSupported = isNotSupported;
        IsIgnored = isIgnored;
    }
    /// <summary>
    /// Indicates that searching is not supported for this field type.
    /// </summary>
    public bool IsNotSupported { get; }
    /// <summary>
    /// Indicates that this field is excluded from searches that do not explicitly name their fields.
    /// </summary>
    public bool IsIgnored { get; }
}
