using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Services;
using Monica.Locker.EfCore;
using Monica.Locker.Leases;

namespace Monica.Repository.Outbox.Services;

internal interface IOutboxStore
{
    string StoreName { get; }
    string? SharedStoreKey { get; }
    OutboxStorePolicy Policy { get; }
    Task<OutboxStoreIdentity> GetIdentityAsync(CancellationToken cancellationToken);
    TimeSpan PollInterval { get; }
    int BatchSize { get; }
    TimeSpan CleanupInterval { get; }
    TimeSpan StatusInterval { get; }
    TimeSpan MaintenanceRetryInterval { get; }
    Task<int> DrainAsync(int maximumMessages, CancellationToken cancellationToken);
    LeaseCoordinator CreateMaintenanceCoordinator();
    Task<LeaseResource> GetMaintenanceResourceAsync(CancellationToken cancellationToken);
    Task<int> CleanupAsync(LeaseIdentity identity, CancellationToken cancellationToken);
    Task<OutboxStoreStatus> GetStatusAsync(CancellationToken cancellationToken);
}

internal sealed record OutboxStoreStatus(int PendingCount, DateTime? OldestPendingAtUtc, string? LastError);

/// <summary>Claims committed, due messages in one store and conditionally acknowledges successful delivery.</summary>
public sealed partial class OutboxDispatcher<TDbContext>(IServiceScopeFactory scopes, TimeProvider time,
    OutboxRegistration<TDbContext> registration, ILogger<OutboxDispatcher<TDbContext>> logger,
    IDbContextFactory<TDbContext> contextFactory, IExclusiveLeaseStore? maintenanceLeaseStore = null,
    IMeterFactory? meterFactory = null) : IOutboxStore
    where TDbContext : RepositoryDbContext<TDbContext>
{
    private readonly RelationalLeaseStore<TDbContext> _leases = new(contextFactory, time,
        registration.Options.MaintenanceLease.OperationTimeout);
    private readonly IExclusiveLeaseStore? _maintenanceLeases = (registration.Options.MaintenanceLeaseStore, maintenanceLeaseStore) switch
    {
        (null, null) => null,
        (not null, not null) => maintenanceLeaseStore,
        _ => throw new ArgumentException("External maintenance requires both a lease-store selection and its resolved singleton store.")
    };
    private readonly string _holder = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private LeaseResource? _maintenanceResource;
    private (string Schema, string Table)? _tableLocation;
    private LeaseCoordinator CreateCoordinator() => new(_maintenanceLeases ?? _leases, _holder, time, logger,
        registration.Options.MaintenanceLease, meterFactory);

    string IOutboxStore.StoreName => typeof(TDbContext).Name;
    string? IOutboxStore.SharedStoreKey => registration.Options.SharedStoreKey;
    OutboxStorePolicy IOutboxStore.Policy => OutboxStorePolicy.From(registration.Options);
    TimeSpan IOutboxStore.PollInterval => registration.Options.PollInterval;
    int IOutboxStore.BatchSize => registration.Options.BatchSize;
    TimeSpan IOutboxStore.CleanupInterval => registration.Options.CleanupInterval;
    TimeSpan IOutboxStore.StatusInterval => registration.Options.StatusInterval;
    TimeSpan IOutboxStore.MaintenanceRetryInterval => registration.Options.MaintenanceLease.StandbyInterval;

    LeaseCoordinator IOutboxStore.CreateMaintenanceCoordinator() => CreateCoordinator();

    Task<LeaseResource> IOutboxStore.GetMaintenanceResourceAsync(CancellationToken cancellationToken) =>
        GetMaintenanceResourceAsync(cancellationToken);

    async Task<OutboxStoreIdentity> IOutboxStore.GetIdentityAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        // Private SQLite memory connections are distinct stores even with identical connection strings.
        if (connection.GetType().FullName == "Microsoft.Data.Sqlite.SqliteConnection")
        {
            var settings = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection.ConnectionString };
            settings.TryGetValue("Mode", out var mode);
            settings.TryGetValue("Cache", out var cache);
            if (connection.DataSource is "" or ":memory:"
                || string.Equals(mode?.ToString(), "Memory", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(cache?.ToString(), "Shared", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A shared Outbox requires a persistent or explicitly shared named SQLite database.");
        }
        var location = await GetTableLocationAsync(db, cancellationToken);
        return new OutboxStoreIdentity(db.Database.ProviderName!, connection.DataSource, connection.Database,
            location.Schema, location.Table);
    }

    private async Task<LeaseResource> GetMaintenanceResourceAsync(CancellationToken cancellationToken)
    {
        if (_maintenanceResource is { } resource) return resource;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (schema, table) = await GetTableLocationAsync(db, cancellationToken);
        var prefix = registration.Options.MaintenanceLeaseStore is null ? "maintenance" : "outbox:maintenance";
        resource = new LeaseResource(registration.Options.MaintenanceLeaseStore?.Scope ?? "outbox",
            $"{prefix}:{schema.Length}:{schema}:{table.Length}:{table}");
        _maintenanceResource = resource;
        return resource;
    }

    private async Task<(string Schema, string Table)> GetTableLocationAsync(TDbContext db, CancellationToken cancellationToken)
    {
        if (_tableLocation is { } location) return location;
        var model = db.Model.FindEntityType(typeof(OutboxMessage))!;
        var table = model.GetTableName()!;
        var schema = model.GetSchema() ?? string.Empty;
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Npgsql", StringComparison.Ordinal) || provider.Contains("Gauss", StringComparison.Ordinal))
        {
            // Resolve search_path once so different context CLR types protecting the same physical table contend.
            var name = db.GetService<ISqlGenerationHelper>().DelimitIdentifier(table, model.GetSchema());
            schema = await db.Database.SqlQueryRaw<string>("""
                SELECT namespace.nspname::text AS "Value" FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                WHERE relation.oid = to_regclass({0})
                """, name).SingleAsync(cancellationToken);
        }
        location = (schema, table);
        _tableLocation = location;
        return location;
    }

    /// <summary>
    /// Deletes acknowledged rows beyond retention in independently committed, bounded SQL batches.
    /// Stops after a partial batch or when the cleanup time budget is exhausted between statements.
    /// Pending rows are never deleted. Cancellation and database failures propagate to the caller;
    /// batches already committed remain deleted.
    /// </summary>
    /// <remarks>Returns zero when another maintenance owner holds the role. The default SQL authority validates
    /// ownership inside each deletion transaction; an external authority uses repeat-safe autocommitted deletes.
    /// This manual invocation releases its role after completion.</remarks>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        await using var coordinator = CreateCoordinator();
        var context = await coordinator.GetAsync(await GetMaintenanceResourceAsync(cancellationToken), cancellationToken);
        if (context is null) return 0;
        using var term = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StoppingToken);
        return await CleanupAsync(context.Identity, term.Token);
    }

    Task<int> IOutboxStore.CleanupAsync(LeaseIdentity identity, CancellationToken cancellationToken) =>
        CleanupAsync(identity, cancellationToken);

    private async Task<int> CleanupAsync(LeaseIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.Resource != await GetMaintenanceResourceAsync(cancellationToken))
            throw new ArgumentException("The lease must identify this physical outbox table's maintenance role.", nameof(identity));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var options = registration.Options;
        db.Database.SetCommandTimeout(options.MaintenanceLease.OperationTimeout);
        var cutoff = time.GetUtcNow().UtcDateTime - options.DeliveredRetention;
        var rows = db.Set<OutboxMessage>();
        // The existing delivery index starts with DeliveredAtUtc. Ordering by Sequence instead
        // can force primary-key scans through retained rows or an extra sort of expired rows.
        var keys = rows
            .Where(x => x.DeliveredAtUtc != null && x.DeliveredAtUtc < cutoff)
            .OrderBy(x => x.DeliveredAtUtc).Select(x => x.Sequence)
            .Take(options.CleanupBatchSize);
        var started = time.GetTimestamp();
        var deleted = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.MaintenanceLease.OperationTimeout);
            // External coordination is advisory: the single bounded DELETE is safe to repeat and commits itself.
            // A Redis read followed by a SQL write would not fence anything, so do not pretend to validate it here.
            await using var transaction = _maintenanceLeases is null
                ? await db.Database.BeginTransactionAsync(deadline.Token) : null;
            if (transaction is not null) await _leases.EnsureHeldAsync(db, identity, deadline.Token);
            var count = await rows.Where(x => keys.Contains(x.Sequence)).ExecuteDeleteAsync(deadline.Token);
            if (transaction is not null) await transaction.CommitAsync(deadline.Token);
            deleted += count;
            if (count < options.CleanupBatchSize) break;
        } while (time.GetElapsedTime(started) < options.CleanupTimeBudget);
        return deleted;
    }

    async Task<OutboxStoreStatus> IOutboxStore.GetStatusAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(registration.Options.MaintenanceLease.OperationTimeout);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        db.Database.SetCommandTimeout(registration.Options.MaintenanceLease.OperationTimeout);
        var rows = db.Set<OutboxMessage>().AsNoTracking();
        var pending = rows.Where(x => x.DeliveredAtUtc == null);
        var summary = await pending.GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Oldest = group.Min(x => x.CreatedAtUtc) })
            .FirstOrDefaultAsync(deadline.Token);
        if (summary is null) return new OutboxStoreStatus(0, null, null);
        var error = await pending.Where(x => x.LastError != null)
            .OrderByDescending(x => x.Sequence).Select(x => x.LastError)
            .FirstOrDefaultAsync(deadline.Token);
        return new OutboxStoreStatus(summary.Count, summary.Oldest, error);
    }

}
