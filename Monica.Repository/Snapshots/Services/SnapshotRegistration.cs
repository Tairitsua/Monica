using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Repository.Persistence.Models;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Models;

namespace Monica.Repository.Snapshots.Services;

internal sealed record SnapshotDataset(string Key, IReadOnlyList<Type> Dependencies)
{
    public bool DependsOn(Type type) => Dependencies.Any(dependency => dependency.IsAssignableFrom(type));
}
internal sealed record SnapshotProjection(SnapshotDataset Dataset, string Name, int AlgorithmVersion, int FormatVersion);
internal sealed record SnapshotTableRegistration<TContext>(bool ExcludeFromMigrations);
internal sealed record SnapshotProjectionRegistration(Type ContextType, Type ResultType, string SourceId, string DatasetKey, string Name);

internal static class SnapshotNames
{
    public static void Validate(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 200) throw new ArgumentOutOfRangeException(name, "Snapshot identifiers must not exceed 200 characters.");
    }
}

internal sealed record SnapshotRegistration<TContext>(string SourceId, IReadOnlyList<SnapshotDataset> Datasets,
    int MaximumCachedEntries, int MaximumConcurrentBuilds, int MaximumPendingBuilds,
    TimeSpan BuildTimeout, int MaximumBuildAttempts) where TContext : DbContext;

internal sealed class SnapshotRegistrationValidator<TContext>(IServiceProvider services,
    SnapshotRegistration<TContext> registration) : IHostedService where TContext : RepositoryDbContext<TContext>
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!services.GetServices<RepositoryDbContextRegistration>().Any(x => x.DbContextType == typeof(TContext)
            && x.ProviderType == DbContextProviderType.UnitOfWork))
            throw new InvalidOperationException($"Snapshots require a managed UnitOfWork registration for '{typeof(TContext)}'.");
        await using var context = await services.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContextAsync(cancellationToken);
        if (!context.Database.IsRelational()) throw new NotSupportedException("Repository snapshots require a relational provider.");
        foreach (var dependency in registration.Datasets.SelectMany(x => x.Dependencies).Distinct())
            if (!context.Model.GetEntityTypes().Any(x => dependency.IsAssignableFrom(x.ClrType)))
                throw new InvalidOperationException($"Snapshot dependency '{dependency}' is not mapped by '{typeof(TContext)}'.");
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
