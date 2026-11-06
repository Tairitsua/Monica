namespace Monica.ModelQuery.Models;

/// <summary>
/// Host-owned metadata for one model type and its queryable fields. Consumers should not mutate cached snapshots.
/// </summary>
public class ModelQuerySnapshot
{
    /// <summary>Gets or sets the queryable fields discovered for the model.</summary>
    public List<QueryField> Fields { get; set; } = [];
    /// <summary>Gets or sets the model identity and display metadata.</summary>
    public QueryModel Model { get; set; } = new() { FullTypeName = string.Empty };
}
