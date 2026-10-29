using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Models;
using Monica.Repository.Snapshots.Services;
using Monica.Repository.Snapshots.Metrics;

namespace Monica.Modules;

/// <summary>Opt-in transactional dataset versions and immutable repository projections. No cache messages or external cache are registered.</summary>
public static class ModuleRepositorySnapshotExtensions
{
    /// <summary>Maps the shared technical version table, optionally excluding it from this context's migrations. Does not create it.</summary>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddSnapshotVersionTable<TContext>(
        this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module, bool excludeFromMigrations = false)
        where TContext : RepositoryDbContext<TContext>
    {
        module.ConfigureServices(context =>
        {
            if (context.Services.Any(x => x.ServiceType == typeof(SnapshotTableRegistration<TContext>)))
                throw new InvalidOperationException($"Snapshot version table for '{typeof(TContext)}' is already configured.");
            context.Services.AddSingleton(new SnapshotTableRegistration<TContext>(excludeFromMigrations));
        });
        return module;
    }

    /// <summary>Registers version maintenance and optional projections for a registered managed context. Prepare schema and version rows before reads/writes.</summary>
    /// <remarks>Every writer must register complete dataset dependencies. Only immutable, explicitly parameterized projections may be shared.</remarks>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> AddSnapshots<TContext>(
        this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module,
        Action<RepositorySnapshotOptions<TContext>> configure) where TContext : RepositoryDbContext<TContext>
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RepositorySnapshotOptions<TContext>();
        configure(options);
        SnapshotNames.Validate(options.SourceId, nameof(options.SourceId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumCachedEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConcurrentBuilds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPendingBuilds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBuildAttempts);
        if (options.BuildTimeout <= TimeSpan.Zero || options.BuildTimeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(configure), "Build timeout must be positive and at most one day.");
        if (options.Datasets.Count == 0) throw new InvalidOperationException("Snapshots require at least one dataset.");
        var projections = options.Projections.Select(definition =>
        {
            var builder = new RepositorySnapshotBuilder<TContext>();
            definition.Configure(builder);
            if (builder.DatasetType is null || builder.Name is null || !options.Datasets.TryGetValue(builder.DatasetType, out var dataset))
                throw new InvalidOperationException($"Projection '{definition.GetType()}' must name and reference a registered dataset.");
            return (definition, projection: new SnapshotProjection(dataset, builder.Name, builder.AlgorithmVersion, builder.FormatVersion));
        }).ToArray();
        if (projections.GroupBy(x => x.definition.ResultType).Any(x => x.Count() > 1)
            || projections.GroupBy(x => (x.projection.Dataset.Key, x.projection.Name)).Any(x => x.Count() > 1))
            throw new InvalidOperationException("Snapshot result types and names within a dataset must be unique.");
        var registration = new SnapshotRegistration<TContext>(options.SourceId, options.Datasets.Values.ToArray(),
            options.MaximumCachedEntries, options.MaximumConcurrentBuilds, options.MaximumPendingBuilds,
            options.BuildTimeout, options.MaximumBuildAttempts);
        module.Require<ModuleUnitOfWork, ModuleUnitOfWorkOption>();
        module.AddSnapshotVersionTable<TContext>(options.ExcludeFromMigrations);
        module.ConfigureServices(context =>
        {
            context.Services.AddSingleton(registration);
            context.Services.TryAddScoped<SnapshotTransaction>();
            context.Services.TryAddSingleton<SnapshotMetrics>();
            context.Services.AddSingleton<SnapshotRuntime<TContext>>();
            context.Services.AddSingleton(sp => new RepositorySnapshotAdministration<TContext>(
                sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<TContext>>(), registration,
                sp.GetRequiredService<SnapshotRuntime<TContext>>()));
            context.Services.AddSingleton<IHostedService, SnapshotRegistrationValidator<TContext>>();
            foreach (var (definition, projection) in projections)
            {
                var existing = context.Services.Where(service => service.ServiceType == typeof(SnapshotProjectionRegistration))
                    .Select(service => (SnapshotProjectionRegistration)service.ImplementationInstance!);
                if (existing.Any(value => value.ResultType == definition.ResultType
                    || value.SourceId == registration.SourceId && value.DatasetKey == projection.Dataset.Key && value.Name == projection.Name))
                    throw new InvalidOperationException("Snapshot projection identity or result type is already registered in this host.");
                context.Services.AddSingleton(new SnapshotProjectionRegistration(typeof(TContext), definition.ResultType,
                    registration.SourceId, projection.Dataset.Key, projection.Name));
                definition.Register(context.Services, projection);
            }
        });
        return module;
    }
}
