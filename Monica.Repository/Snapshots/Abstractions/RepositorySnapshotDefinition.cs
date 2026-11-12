using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.Snapshots.Services;

namespace Monica.Repository.Snapshots.Abstractions;

/// <summary>Stateless declaration of the complete source dependencies whose changes advance one version.</summary>
public abstract class RepositoryDatasetDefinition<TContext> where TContext : RepositoryDbContext<TContext>
{
    /// <summary>Names the dataset and declares every entity type read by its projections.</summary>
    public abstract void Configure(RepositoryDatasetBuilder<TContext> dataset);
}

/// <summary>Registration base for stateless projection definitions. Use one of its typed subclasses.</summary>
public abstract class RepositorySnapshotDefinition<TContext> where TContext : RepositoryDbContext<TContext>
{
    /// <summary>Selects a registered dataset and the projection's stable name and code versions.</summary>
    public abstract void Configure(RepositorySnapshotBuilder<TContext> snapshot);
    internal abstract Type ResultType { get; }
    internal abstract void Register(IServiceCollection services, SnapshotProjection projection);
}

/// <summary>A stateless projection with immutable input. The framework owns and disposes the independent loading context.</summary>
public abstract class RepositorySnapshotDefinition<TContext, TInput, TSnapshot> : RepositorySnapshotDefinition<TContext>
    where TContext : RepositoryDbContext<TContext> where TInput : notnull where TSnapshot : class
{
    /// <summary>Validates and normalizes input before both cache identification and loading. Must be deterministic.</summary>
    public virtual TInput Normalize(TInput input) => input;
    /// <summary>Loads a deeply immutable value covering only declared dependencies. Honor cancellation; never retain the context or entities.</summary>
    public abstract Task<TSnapshot> LoadAsync(TContext context, TInput input, CancellationToken cancellationToken);
    internal override Type ResultType => typeof(TSnapshot);
    internal override void Register(IServiceCollection services, SnapshotProjection projection)
    {
        SnapshotInputCodec.Validate(typeof(TInput));
        services.AddScoped<IRepositorySnapshot<TSnapshot, TInput>>(sp =>
            new SnapshotAccessor<TContext, TInput, TSnapshot>(sp.GetRequiredService<TContext>(),
                sp.GetRequiredService<SnapshotTransaction>(), sp.GetRequiredService<SnapshotRuntime<TContext>>(), this, projection));
    }
}

/// <summary>A stateless projection without business parameters.</summary>
public abstract class RepositorySnapshotDefinition<TContext, TSnapshot>
    : RepositorySnapshotDefinition<TContext, ValueTuple, TSnapshot>
    where TContext : RepositoryDbContext<TContext> where TSnapshot : class
{
    /// <summary>Loads immutable data using the independent context supplied by the framework.</summary>
    public abstract Task<TSnapshot> LoadAsync(TContext context, CancellationToken cancellationToken);
    /// <inheritdoc />
    public sealed override Task<TSnapshot> LoadAsync(TContext context, ValueTuple input, CancellationToken cancellationToken)
        => LoadAsync(context, cancellationToken);
    internal override void Register(IServiceCollection services, SnapshotProjection projection)
    {
        base.Register(services, projection);
        services.AddScoped<IRepositorySnapshot<TSnapshot>, SnapshotAccessor<TSnapshot>>();
    }
}
