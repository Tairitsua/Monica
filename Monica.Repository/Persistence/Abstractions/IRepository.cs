using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Entity.Abstractions;

namespace Monica.Repository.Persistence.Abstractions;

/// <summary>
/// Minimal aggregate staging contract. Domain-specific repositories add purposeful tracked loading methods.
/// The operation boundary owns persistence; this interface intentionally exposes no query language or save method.
/// </summary>
public interface IRepository<TEntity> : ITransientDependency where TEntity : class, IEntity
{
    /// <summary>Stages a new aggregate and its new owned graph.</summary>
    void Add(TEntity entity);
    /// <summary>Stages aggregates with the same graph and save-policy semantics as Add; no native bulk execution occurs.</summary>
    void AddRange(IEnumerable<TEntity> entities);
    /// <summary>Stages removal using the aggregate's configured deletion policy.</summary>
    void Remove(TEntity entity);
    /// <summary>Stages aggregate removals under the normal deletion policy; persistence belongs to the operation boundary.</summary>
    void RemoveRange(IEnumerable<TEntity> entities);
}

/// <summary>Loads a single-key aggregate into the current operation's identity map.</summary>
public interface IRepository<TEntity, in TKey> : IRepository<TEntity> where TEntity : class, IEntity<TKey>
{
    /// <summary>Returns a tracked aggregate or null when absent/filtered. Repeated loads preserve object identity.</summary>
    Task<TEntity?> FindAsync(TKey id, CancellationToken cancellationToken = default);
    /// <summary>Returns a tracked aggregate; throws EntityNotFoundException when absent/filtered.</summary>
    Task<TEntity> GetAsync(TKey id, CancellationToken cancellationToken = default);
}
