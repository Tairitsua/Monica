namespace Monica.ModelQuery.Models;

/// <summary>
/// Immutable host-owned metadata for one model type and its queryable fields.
/// </summary>
public sealed class ModelQuerySnapshot
{
    internal ModelQuerySnapshot(QueryModel model, IEnumerable<QueryField> fields)
    {
        Model = model;
        Fields = Array.AsReadOnly(fields.ToArray());
    }
    /// <summary>Gets the queryable fields discovered for the model.</summary>
    public IReadOnlyList<QueryField> Fields { get; }
    /// <summary>Gets the model identity and display metadata.</summary>
    public QueryModel Model { get; }
}
