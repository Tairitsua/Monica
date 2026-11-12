using Microsoft.EntityFrameworkCore;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Models;

namespace Monica.Repository.Snapshots.Services;

/// <summary>Explicit deployment initialization for registered dataset rows. Does not create schemas or reset existing versions.</summary>
public sealed class RepositorySnapshotAdministration<TContext> where TContext : RepositoryDbContext<TContext>
{
    private readonly IDbContextFactory<TContext> _factory;
    private readonly SnapshotRegistration<TContext> _registration;
    private readonly SnapshotRuntime<TContext> _runtime;
    internal RepositorySnapshotAdministration(IDbContextFactory<TContext> factory, SnapshotRegistration<TContext> registration,
        SnapshotRuntime<TContext> runtime)
    {
        _factory = factory;
        _registration = registration;
        _runtime = runtime;
    }

    /// <summary>Reads this host's retained projection identities, validation ages and construction capacity without database I/O.</summary>
    public RepositorySnapshotStatus GetStatus() => _runtime.GetStatus();

    /// <summary>Idempotently creates missing rows after migration. Concurrent initializers retry in fresh scopes; other storage failures propagate.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var present = await db.Set<RepositorySnapshotVersion>().Where(x => x.SourceId == _registration.SourceId)
                .Select(x => x.DatasetKey).ToListAsync(cancellationToken);
            var missing = _registration.Datasets.Where(x => !present.Contains(x.Key, StringComparer.Ordinal)).ToArray();
            if (missing.Length == 0) return;
            db.Set<RepositorySnapshotVersion>().AddRange(missing.Select(dataset => new RepositorySnapshotVersion
            {
                SourceId = _registration.SourceId, DatasetKey = dataset.Key, Epoch = Guid.NewGuid(), Revision = 0
            }));
            try { await db.SaveChangesAsync(cancellationToken); return; }
            catch (DbUpdateException) when (attempt < 2)
            {
                // A competing initializer can win the primary-key race. Recheck without reusing a failed context.
                await using var check = await _factory.CreateDbContextAsync(cancellationToken);
                var keys = await check.Set<RepositorySnapshotVersion>().Where(x => x.SourceId == _registration.SourceId)
                    .Select(x => x.DatasetKey).ToListAsync(cancellationToken);
                if (!missing.Any(x => keys.Contains(x.Key, StringComparer.Ordinal))) throw;
            }
        }
    }
}
