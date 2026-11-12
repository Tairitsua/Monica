using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Monica.Repository.Persistence.Services.Support;

internal interface IRepositoryModelFeatures
{
    bool HasOutbox { get; }
    bool HasInbox { get; }
    bool ExcludeOutboxFromMigrations { get; }
    bool ExcludeInboxFromMigrations { get; }
    bool ExcludeLeasesFromMigrations { get; }
    bool HasSnapshots { get; }
    bool ExcludeSnapshotsFromMigrations { get; }
    object ModelCacheKey { get; }
}

internal sealed class RepositoryModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => context is IRepositoryModelFeatures features
            ? (features.ModelCacheKey, features.HasOutbox, features.HasInbox,
                features.ExcludeOutboxFromMigrations, features.ExcludeInboxFromMigrations,
                features.ExcludeLeasesFromMigrations, features.HasSnapshots, features.ExcludeSnapshotsFromMigrations, designTime)
            : (context.GetType(), false, designTime);
}
