namespace Monica.ModelQuery.Abstractions;

/// <summary>Optional model filter and search parameters supplied by an HTTP request.</summary>
public interface IModelFilterRequest
{
    /// <summary>A ModelQuery filter expression, when supplied.</summary>
    string? Filter { get; set; }
    /// <summary>The search text, when supplied.</summary>
    string? SearchText { get; set; }
    /// <summary>Query names to search; null or empty selects the default searchable fields.</summary>
    string[]? SearchFields { get; set; }
}
