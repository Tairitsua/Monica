namespace Monica.ModelQuery.Abstractions;

public interface IHasRequestFilter
{
    /// <summary>
    /// Dynamic filter expression.
    /// </summary>
    string? Filter { get; set; }
    /// <summary>
    /// Value used for fuzzy matching across multiple fields.
    /// </summary>
    string? Fuzzy { get; set; }
    /// <summary>
    /// Optional field list that limits fuzzy matching.
    /// </summary>
    string? FuzzyColumns { get; set; }
}
