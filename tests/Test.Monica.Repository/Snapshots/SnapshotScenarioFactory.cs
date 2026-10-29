using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Abstractions;
using Monica.Repository.Snapshots.Models;
using Monica.Testing.Hosting;

namespace Test.Monica.Repository.Snapshots;

internal sealed class SnapshotScenarioFactory(ProjectionProbe? probe = null, SnapshotClock? clock = null,
    string? databasePath = null, int maximumEntries = 128,
    Action<RepositorySnapshotOptions<SnapshotTestDbContext>>? configure = null,
    IInterceptor? interceptor = null) : MonicaTestApplicationFactory<SnapshotTestDbContext>
{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.AddRepository()
            .AddRepositoryDbContext<SnapshotTestDbContext>((_, db) =>
            {
                db.UseSqlite($"Data Source={databasePath ?? "unused"};Pooling=False");
                if (interceptor is not null) db.AddInterceptors(interceptor);
            })
            .AddSnapshots<SnapshotTestDbContext>(options =>
            {
                options.SourceId = "tests/site-a";
                options.MaximumCachedEntries = maximumEntries;
                options.AddDataset<MaterialDefinition>();
                options.AddProjection<CatalogDefinition>();
                options.AddProjection<MapDefinition>();
                configure?.Invoke(options);
            });
    }
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        if (databasePath is null) services.UseTestDatabase<SnapshotTestDbContext>((_, db) =>
        {
            if (interceptor is not null) db.AddInterceptors(interceptor);
        });
        services.AddSingleton(probe ?? new ProjectionProbe());
        if (clock is not null) services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
    }
}

public sealed class SnapshotTestDbContext(DbContextOptions<SnapshotTestDbContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<SnapshotTestDbContext>(options, services)
{
    public DbSet<MaterialRow> Rows => Set<MaterialRow>();
    public DbSet<MaterialPoint> Points => Set<MaterialPoint>();
}
public sealed class MaterialRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
public sealed class MaterialPoint
{
    public int Id { get; set; }
    public string Value { get; set; } = string.Empty;
}
public sealed record Catalog(ImmutableArray<string> Names);
public sealed record MapInput(DateOnly BusinessDate);
public sealed record MapResult(string Content);
public sealed record ScalarInput(double Value);
public sealed record ScalarResult(double Value);

public sealed class ScalarDefinition : RepositorySnapshotDefinition<SnapshotTestDbContext, ScalarInput, ScalarResult>
{
    public override void Configure(RepositorySnapshotBuilder<SnapshotTestDbContext> snapshot)
        => snapshot.Dataset<MaterialDefinition>().Projection("scalar", 1, 1);
    public override Task<ScalarResult> LoadAsync(SnapshotTestDbContext context, ScalarInput input, CancellationToken cancellationToken)
        => Task.FromResult(new ScalarResult(input.Value));
}

public sealed class MaterialDefinition : RepositoryDatasetDefinition<SnapshotTestDbContext>
{
    public override void Configure(RepositoryDatasetBuilder<SnapshotTestDbContext> dataset)
        => dataset.Dataset("material").DependsOn<MaterialRow>().DependsOn<MaterialPoint>();
}
public sealed class CatalogDefinition : RepositorySnapshotDefinition<SnapshotTestDbContext, Catalog>
{
    public override void Configure(RepositorySnapshotBuilder<SnapshotTestDbContext> snapshot)
        => snapshot.Dataset<MaterialDefinition>().Projection("catalog", 1, 1);
    public override async Task<Catalog> LoadAsync(SnapshotTestDbContext context, CancellationToken cancellationToken)
    {
        var rows = await context.Rows.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Name).ToListAsync(cancellationToken);
        await context.CachedServiceProvider.GetRequiredService<ProjectionProbe>().LoadedAsync(cancellationToken);
        return new(rows.ToImmutableArray());
    }
}
public sealed class MapDefinition : RepositorySnapshotDefinition<SnapshotTestDbContext, MapInput, MapResult>
{
    public override void Configure(RepositorySnapshotBuilder<SnapshotTestDbContext> snapshot)
        => snapshot.Dataset<MaterialDefinition>().Projection("map", 3, 1);
    public override async Task<MapResult> LoadAsync(SnapshotTestDbContext context, MapInput input, CancellationToken cancellationToken)
    {
        var names = await context.Rows.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Name).ToListAsync(cancellationToken);
        return new($"{input.BusinessDate:yyyy-MM-dd}:{string.Join(',', names)}");
    }
}

internal sealed class ProjectionProbe
{
    private int _count;
    public int Count => Volatile.Read(ref _count);
    public bool HoldFirst { get; init; }
    public bool Fail { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task LoadedAsync(CancellationToken token)
    {
        if (Fail) throw new ApplicationException("Projection construction unavailable.");
        if (Interlocked.Increment(ref _count) == 1 && HoldFirst)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }
}

internal sealed class SnapshotClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Volatile.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

internal sealed class SlowValidationInterceptor(SnapshotClock clock) : DbCommandInterceptor
{
    public bool Enabled { get; set; }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (Enabled && command.CommandText.Contains("MonicaSnapshotVersions", StringComparison.Ordinal))
            clock.Advance(TimeSpan.FromSeconds(2));
        return ValueTask.FromResult(result);
    }
}
