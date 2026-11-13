namespace Monica.Framework.ChangeTracking.Models;

/// <summary>Orders accepted changes by business time, preserved historical position, and stable identity.</summary>
/// <param name="TimeTicks">Ticks of the effective time, including the historical microsecond adjustment.</param>
/// <param name="HistoricalOrder">Frozen position from a legacy document, or null for newly accepted changes.</param>
/// <param name="EntryId">Stable business identity, compared using ordinal string order.</param>
/// <remarks>Imported entries precede new entries at the same time. Commit sequence is deliberately excluded.</remarks>
public readonly record struct ChangeOrderKey(long TimeTicks, long? HistoricalOrder, string EntryId)
    : IComparable<ChangeOrderKey>
{
    /// <inheritdoc />
    public int CompareTo(ChangeOrderKey other)
    {
        var comparison = TimeTicks.CompareTo(other.TimeTicks);
        if (comparison != 0) return comparison;
        comparison = (HistoricalOrder ?? long.MaxValue).CompareTo(other.HistoricalOrder ?? long.MaxValue);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(EntryId, other.EntryId);
    }
}
