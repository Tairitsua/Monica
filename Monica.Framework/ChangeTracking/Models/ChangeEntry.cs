using System.ComponentModel.DataAnnotations.Schema;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Monica.Framework.ChangeTracking.Abstractions;
using Monica.Tool.Extensions;

namespace Monica.Framework.ChangeTracking.Models;


public interface IChangeEntry
{
    static readonly JsonSerializerOptions ALTER_ITEM_SERIALIZER_OPTIONS = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public class ChangeEntry<TTargetEntity, TChangeItemData, TEnumAlterSource> : IChangeEntry where TChangeItemData : class, IChangeData<TTargetEntity>, new() where TTargetEntity : class, IChangeTrackedEntity where TEnumAlterSource:Enum
{
    private const string EmptyValuePlaceholder = "[空值]";

    /// <summary>
    /// Change item ID
    /// </summary>
    public string Id { get; set; }


    #region Change data

    private string _dataJson = "{}";
    /// <summary>The serialized accepted change; replacing it invalidates the parsed data.</summary>
    public string DataJson
    {
        get => _dataJson;
        set { _dataJson = value; _data = null; }
    }
    private TChangeItemData? _data;


    [NotMapped]
    [JsonIgnore]
    public TChangeItemData Data
    {
        get => _data ??= JsonSerializer.Deserialize<TChangeItemData>(DataJson, IChangeEntry.ALTER_ITEM_SERIALIZER_OPTIONS)
            ?? throw new InvalidOperationException("The accepted change data is null.");
        set
        {
            _data = value;
            RefreshDataJson();
        }
    }

    /// <summary>
    /// Refresh DataJson, must be called when Data changes, otherwise it will not take effect
    /// </summary>
    public void RefreshDataJson()
    {
        if (_data is not null) _dataJson = JsonSerializer.Serialize(_data, IChangeEntry.ALTER_ITEM_SERIALIZER_OPTIONS);
    }

    #endregion

    #region Rollback
    /// <summary>
    /// Target rollback ID
    /// </summary>
    public List<string>? TargetRollbackIds { get; set; }

    /// <summary>Whether this entry rolls back a nonempty set of accepted changes.</summary>
    [NotMapped, JsonIgnore]
    public bool IsRollback => TargetRollbackIds is { Count: > 0 };

    /// <summary>
    /// Whether it has been rolled back (invalidation flag)
    /// </summary>
    public bool IsInvalid => RollbackBy is not null;

    /// <summary>
    /// (If it has a value, it is considered to have been rolled back) The rollback modification item ID that causes the modification item to be rolled back
    /// </summary>
    public string? RollbackBy { get; set; }

    public void DoRollback(string rollbackBy)
    {
        RollbackBy = rollbackBy;
    }

    public void CancelRollback()
    {
        RollbackBy = null;
    }

    #endregion

    #region Setting
    /// <summary>
    /// The change operation time
    /// </summary>
    public DateTime AlterTime { get; set; }

    /// <summary>
    /// The actual change operation impact time, the default is AlterTime
    /// </summary>
    public DateTime? ActualEffectTime { get; set; }

    /// <summary>
    /// The time used when sorting is also the time that is actually applied to the modification.
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    public DateTime OrderTime => (ActualEffectTime ?? AlterTime).AddMicroseconds(OffsetAdjust);

    /// <summary>Preserves the exact ordering time when a transport rounds display timestamps.</summary>
    /// <remarks>Set once when accepting an entry. Null derives the time from the original timestamp and adjustment.</remarks>
    public long? SortTimeTicks { get; set; }

    /// <summary>Frozen legacy document position, or null for new entries ordered by stable identity.</summary>
    public long? HistoricalOrder { get; set; }

    /// <summary>The complete business ordering key; independent of database commit order.</summary>
    [NotMapped, JsonIgnore]
    public ChangeOrderKey OrderKey => new(SortTimeTicks ?? OrderTime.Ticks, HistoricalOrder, Id);

    /// <summary>
    /// Fine-tune modification time to solve extreme situations such as duplication of change operation time
    /// </summary>
    public int OffsetAdjust { get; set; }

    /// <summary>
    /// Change notes
    /// </summary>
    public string? Remarks { get; set; }
    /// <summary>
    /// Change source
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TEnumAlterSource Source { get; set; } = default!;
    /// <summary>
    /// Change source name
    /// </summary>
    public string? SourceInfo { get; set; }
    /// <summary>
    /// Change source GUID, such as user GUID, etc.
    /// </summary>
    public string? SourceId { get; set; }

    /// <summary>
    /// Change related operation source group ID, which can be used to roll back by affected group
    /// </summary>
    [JsonPropertyName("RelatedSourceId")]
    public List<string>? RelatedSourceIds { get; set; }
    #endregion

    public ChangeEntry(string id, TEnumAlterSource source, DateTime alterTime, TChangeItemData data, string? sourceId, string? sourceInfo = null)
    {
        Id = id;
        Source = source;
        AlterTime = alterTime;
        DataJson = null!;
        Data = data;
        SourceId = sourceId;
        SourceInfo = sourceInfo;
    }

    public ChangeEntry(string id, TEnumAlterSource source, DateTime alterTime, List<string> targetRollbackIds, string? sourceId, string? sourceInfo = null)
        : this(id, source, alterTime, new TChangeItemData(), sourceId, sourceInfo)
    {
        TargetRollbackIds = targetRollbackIds;
    }

    protected ChangeEntry()
    {
        Id = "";
    }

    /// <summary>
    /// Apply changes
    /// </summary>
    public TTargetEntity ApplyAlter(TTargetEntity entity)
    {
        if (IsInvalid || IsRollback) return entity;
        Data.Apply(entity);
        return entity;
    }
    
    /// <summary>
    /// Get the change description from the original state to apply this change
    /// </summary>
    /// <param name="previousStatus"></param>
    /// <returns></returns>
    public List<ChangeRecord> GetChangesFromPreviousStatus(TTargetEntity previousStatus)
    {
        var list = new List<ChangeRecord>();
        if (IsRollback)
        {
            list.AddIfNotNull(new ChangeRecord()
            {
                IsRollback = IsInvalid,
                DisplayName = "回滚修改",
                TargetRollbackIds = TargetRollbackIds
            });
        }
        else
        {
            foreach (var propertyAlterData in Data.GetChanges(previousStatus))
            {
                list.AddIfNotNull(Format(propertyAlterData.DisplayName, propertyAlterData.OldValue, propertyAlterData.NewValue));
            }
            
            static ChangeRecord? Format(string displayName, object? oldValue, object? newValue)
            {
                if (newValue == null || oldValue == newValue)
                {
                    return null;
                }

                if (oldValue is not null)
                {
                    if (oldValue is string originStr && newValue is string newStr && originStr == newStr)
                    {
                        return null;
                    }
                    if (oldValue is DateTime originDateTime && newValue is DateTime newDateTime && originDateTime.EqualBySecond(newDateTime))
                    {
                        return null;
                    }

                    if (oldValue is TimeSpan originTimeSpan && newValue is TimeSpan newTimeSpan && originTimeSpan == newTimeSpan)
                    {
                        return null;
                    }

                    if (oldValue is bool originBool && newValue is bool newBool && originBool == newBool)
                    {
                        return null;
                    }

                    if (oldValue is int originInt && newValue is int newInt && originInt == newInt)
                    {
                        return null;
                    }
                }


                if (oldValue is Enum oriEnum && newValue is Enum newEnum)
                {
                    if (Equals(oriEnum, newEnum))
                    {
                        return null;
                    }

                    return new ChangeRecord()
                    {
                        DisplayName = displayName,
                        OldValue = oriEnum.ToString(),
                        NewValue = newEnum.ToString()
                    };
                }
                var record = new ChangeRecord()
                {
                    DisplayName = displayName,
                    OldValue = oldValue,
                    NewValue = newValue
                };

                if (record.OldValue is DateTime dateTimeValue && dateTimeValue == default(DateTime))
                {
                    record.OldValue = null;
                }
                if (record.NewValue is DateTime newTimeValue && newTimeValue == default(DateTime))
                {
                    record.NewValue = null;
                }

                record.OldValue = NormalizeDisplayValue(record.OldValue);
                record.NewValue = NormalizeDisplayValue(record.NewValue);

                return record;
            }

            static object NormalizeDisplayValue(object? value)
            {
                return value switch
                {
                    null => EmptyValuePlaceholder,
                    string text when string.IsNullOrWhiteSpace(text) => EmptyValuePlaceholder,
                    _ => value
                };
            }
        }

        return list;
    }
    
    
    public override string ToString()
    {
        string data;
        if (IsRollback)
        {
            data = $"回滚修改{TargetRollbackIds.StringJoin(",")}";
        }
        else
        {
            var sb = new StringBuilder();
            
            foreach (var propertyAlterData in Data.GetChanges())
            {
                sb.Append(Format(propertyAlterData.DisplayName, propertyAlterData.NewValue));
            }

            data = sb.ToString().TrimEnd();
            static string? Format(string name, object? newValue)
            {
                return newValue == null ? null : $"{name}变更为{newValue.ToString()?.BeNullIfEmpty() ?? "[空值]"}\n";
            }
        }

        return IsInvalid ? $"[修改已被{RollbackBy}回滚]\n{data}" : data;
    }

}
