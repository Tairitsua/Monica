namespace Monica.Framework.ChangeTracking.Models;

public record ChangeRecord
{
    /// <summary>
    /// Change record name
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Old value (from Entity)
    /// </summary>
    public object? OldValue { get; set; }

    /// <summary>
    /// New value (from AlterItem)
    /// </summary>
    public object? NewValue { get; set; }
    /// <summary>
    /// Whether to roll back
    /// </summary>
    public bool? IsRollback { get; set; }

    /// <summary>
    /// Target rollback ID
    /// </summary>
    public List<Guid>? TargetRollbackIds { get; set; }
}