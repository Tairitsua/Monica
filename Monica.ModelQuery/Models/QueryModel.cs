namespace Monica.ModelQuery.Models;

/// <summary>
/// Metadata describing a model whose properties are available for queries.
/// </summary>
public sealed class QueryModel
{
    internal QueryModel(string fullTypeName, string? name)
    {
        FullTypeName = fullTypeName;
        Name = name;
    }
    /// <summary>Gets the full CLR type name for the model.</summary>
    public string FullTypeName { get; }
    /// <summary>Gets the model display name, defaulting to the CLR short type name.</summary>
    public string? Name { get; }
}
