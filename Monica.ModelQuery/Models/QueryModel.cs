namespace Monica.ModelQuery.Models;

/// <summary>
/// Metadata describing a model whose properties are available for queries.
/// </summary>
public class QueryModel
{
    /// <summary>Gets or sets the full CLR type name for the model.</summary>
    public required string FullTypeName { get; set; }
    /// <summary>Gets or sets the model display name, defaulting to the CLR short type name.</summary>
    public string? Name { get; set; }
}
