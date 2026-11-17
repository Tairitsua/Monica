using System.Text.Json.Serialization;
using Monica.Tool.Extensions;

namespace Monica.Repository.Entity.Abstractions;

[Serializable]
public abstract class Entity : IEntity
{
    public override string ToString()
    {
        return $"[ENTITY: {GetType().Name}] Keys = {GetKeys().JoinAsString(", ")}";
    }
    public abstract object?[] GetKeys();

}

[Serializable]
public abstract class Entity<TKey> : Entity, IEntity<TKey>
{
    /// <summary>
    /// Identity assigned by the creation boundary and preserved during loading, retries and replication.
    /// </summary>
    [JsonInclude]
   // [Key]
    public TKey Id { get; set; } = default!;

    public override string ToString()
    {
        return $"[ENTITY: {GetType().Name}] Id = {Id}";
    }

    public override object?[] GetKeys()
    {
        return [Id];
    }
}
