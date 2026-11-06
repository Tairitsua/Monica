namespace Monica.ModelQuery.Models;

/// <summary>An immutable inclusion or exclusion of model query names.</summary>
public sealed class FieldSelection
{
    private FieldSelection(bool isExclusion, string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Field names must not be blank.", nameof(names));
        IsExclusion = isExclusion;
        Names = Array.AsReadOnly((string[])names.Clone());
    }
    /// <summary>Whether the named fields are excluded instead of included.</summary>
    public bool IsExclusion { get; }
    /// <summary>The copied query names supplied for this selection.</summary>
    public IReadOnlyList<string> Names { get; }
    /// <summary>Includes the named query fields.</summary>
    public static FieldSelection Include(params string[] names) => new(false, names);
    /// <summary>Excludes the named query fields.</summary>
    public static FieldSelection Exclude(params string[] names) => new(true, names);
}

/// <summary>The results of independently matching supplied names against model fields.</summary>
public sealed class FieldMatchResult
{
    internal FieldMatchResult(IEnumerable<QueryField> matchedFields, IEnumerable<string> unmatchedNames)
    {
        MatchedFields = Array.AsReadOnly(matchedFields.ToArray());
        UnmatchedNames = Array.AsReadOnly(unmatchedNames.ToArray());
    }
    /// <summary>Fields that matched a supplied name, in input order.</summary>
    public IReadOnlyList<QueryField> MatchedFields { get; }
    /// <summary>Names that did not match, in input order.</summary>
    public IReadOnlyList<string> UnmatchedNames { get; }
}
