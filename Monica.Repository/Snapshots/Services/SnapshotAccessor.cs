using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Abstractions;
using Monica.Repository.Snapshots.Models;

namespace Monica.Repository.Snapshots.Services;

internal sealed class SnapshotAccessor<TContext, TInput, TSnapshot>(TContext context, SnapshotTransaction transaction,
    SnapshotRuntime<TContext> runtime, RepositorySnapshotDefinition<TContext, TInput, TSnapshot> definition,
    SnapshotProjection projection) : IRepositorySnapshot<TSnapshot, TInput>
    where TContext : RepositoryDbContext<TContext> where TInput : notnull where TSnapshot : class
{
    public Task<SnapshotRead<TSnapshot>> ReadFreshAsync(TInput input, CancellationToken cancellationToken = default)
        => ReadAsync(input, null, cancellationToken);
    public Task<SnapshotRead<TSnapshot>> ReadBoundedAsync(TInput input, TimeSpan maxValidationAge, CancellationToken cancellationToken = default)
    {
        if (maxValidationAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxValidationAge));
        return ReadAsync(input, maxValidationAge, cancellationToken);
    }
    private Task<SnapshotRead<TSnapshot>> ReadAsync(TInput input, TimeSpan? age, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        transaction.AssertReadable(context, runtime.Registration, projection.Dataset);
        var normalized = definition.Normalize(input);
        ArgumentNullException.ThrowIfNull(normalized);
        var canonical = SnapshotInputCodec.Encode(normalized);
        var captured = SnapshotInputCodec.Capture<TInput>(canonical);
        var identity = new SnapshotProjectionIdentity(projection.Name, projection.AlgorithmVersion,
            projection.FormatVersion, canonical);
        return runtime.ReadAsync(projection.Dataset, identity,
            (db, ct) => definition.LoadAsync(db, captured, ct), age, token);
    }
}

internal sealed class SnapshotAccessor<T>(IRepositorySnapshot<T, ValueTuple> parameterized) : IRepositorySnapshot<T> where T : class
{
    public Task<SnapshotRead<T>> ReadFreshAsync(CancellationToken cancellationToken = default)
        => parameterized.ReadFreshAsync(default, cancellationToken);
    public Task<SnapshotRead<T>> ReadBoundedAsync(TimeSpan maxValidationAge, CancellationToken cancellationToken = default)
        => parameterized.ReadBoundedAsync(default, maxValidationAge, cancellationToken);
}
