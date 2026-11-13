namespace Monica.Framework.ChangeTracking.Abstractions;

public interface IChangeData<in TEntity> where TEntity : class, IChangeTrackedEntity
{
    /// <summary>
    /// Apply current changes
    /// </summary>
    /// <param name="entity"></param>
    void Apply(TEntity entity);
    
    /// <summary>
    /// Get current change information
    /// </summary>
    /// <param name="entity">If this value is passed in, the entity value related to the change will be returned.</param>
    /// <returns></returns>
    IEnumerable<PropertyChange> GetChanges(TEntity? entity = null);
}

/// <summary>
/// Property change information
/// </summary>
public class PropertyChange
{
    /// <summary>
    /// Change display name. The Title of ChangeEntryPropertyAttribute is used first; otherwise PropertyName is used.
    /// </summary>
    public required string DisplayName { get; set; }
    /// <summary>
    /// Change attribute name
    /// </summary>
    public required string PropertyName { get; set; }
    /// <summary>
    /// The associated value from the Entity
    /// </summary>
    public object? OldValue { get; set; }
    /// <summary>
    /// The associated value from PropertyChange
    /// </summary>
    public object? NewValue { get; set; }
}
