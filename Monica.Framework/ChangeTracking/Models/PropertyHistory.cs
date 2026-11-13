
namespace Monica.Framework.ChangeTracking.Models;

/// <summary>
/// Change record attribute history
/// </summary>
/// <typeparam name="TAlterItem"></typeparam>
/// <param name="BeforeValue"></param>
/// <param name="CurrentValue"></param>
/// <param name="RelatedAlterItem"></param>
public record PropertyHistory<TAlterItem>(object? BeforeValue, object? CurrentValue, TAlterItem? RelatedAlterItem)
    where TAlterItem : IChangeEntry
{
    /// <summary>
    /// is the initial value
    /// </summary>
    public bool IsInitialValue => RelatedAlterItem == null;

}