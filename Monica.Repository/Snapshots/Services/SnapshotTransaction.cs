using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.UnitOfWork.Services;

namespace Monica.Repository.Snapshots.Services;

/// <summary>One scoped set across all participants and flushes of a managed physical transaction.</summary>
internal sealed class SnapshotTransaction(IServiceProvider services)
{
    private readonly Dictionary<(string Source, string Dataset), DbContext> _affected = [];
    private bool _finalized;

    public void Capture<TContext>(DbContext context, SnapshotRegistration<TContext> registration, IEnumerable<Type> entityTypes)
        where TContext : DbContext
    {
        var types = entityTypes.Distinct().ToArray();
        var affected = registration.Datasets.Where(dataset => types.Any(dataset.DependsOn)).ToArray();
        if (affected.Length == 0) return;
        if (_finalized) throw new InvalidOperationException("Business writes cannot follow snapshot version finalization.");
        var manager = services.GetRequiredService<UnitOfWorkManager>();
        manager.ValidateSave(context);
        if (!manager.IsActive || context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Snapshot dataset writes require a Monica-owned UnitOfWork transaction.");
        foreach (var dataset in affected) _affected.TryAdd((registration.SourceId, dataset.Key), context);
    }

    public void AssertReadable<TContext>(TContext context, SnapshotRegistration<TContext> registration, SnapshotDataset dataset)
        where TContext : RepositoryDbContext<TContext>
    {
        context.ValidateDatabaseAccess();
        if (HasChanges(context, registration.SourceId, dataset))
            throw new InvalidOperationException($"Dataset '{dataset.Key}' has uncommitted changes in this operation. Read the operation's current state instead of a shared snapshot.");
    }

    public bool HasChanges(DbContext context, string sourceId, SnapshotDataset dataset)
    {
        var manager = services.GetRequiredService<UnitOfWorkManager>();
        var contexts = manager.ParticipatingContexts.Append(context).Distinct();
        return _affected.ContainsKey((sourceId, dataset.Key)) || contexts.Any(db => db.ChangeTracker.Entries()
            .Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && dataset.DependsOn(entry.Metadata.ClrType)));
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        if (_finalized) throw new InvalidOperationException("Snapshot versions were already finalized.");
        _finalized = true;
        foreach (var (key, context) in _affected.OrderBy(x => x.Key.Source, StringComparer.Ordinal)
                     .ThenBy(x => x.Key.Dataset, StringComparer.Ordinal))
        {
            var changed = await context.Set<RepositorySnapshotVersion>()
                .Where(row => row.SourceId == key.Source && row.DatasetKey == key.Dataset
                    && row.Revision >= 0 && row.Revision < long.MaxValue && row.Epoch != Guid.Empty)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Revision, row => row.Revision + 1), cancellationToken);
            if (changed != 1)
                throw new InvalidOperationException($"Snapshot authority '{key.Source}/{key.Dataset}' is missing, invalid or exhausted. Initialize or repair it before accepting dataset writes.");
        }
    }
}
