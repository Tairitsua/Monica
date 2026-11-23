namespace Monica.WebApi.Annotations;

/// <summary>
/// Declares the full row type for a custom CRUD action returning a dynamic page or object stream.
/// The standard CRUD list and stream actions supply this metadata automatically.
/// </summary>
/// <param name="rowType">The full row when the input does not select individual fields.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CrudCollectionResponseAttribute(Type rowType) : Attribute
{
    /// <summary>Gets the full row type to document in OpenAPI.</summary>
    public Type RowType { get; } = rowType;
}
