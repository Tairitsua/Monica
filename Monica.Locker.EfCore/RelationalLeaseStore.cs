using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Monica.Locker.EfCore.Internal;
using Monica.Locker.Leases;

namespace Monica.Locker.EfCore;

/// <summary>Implements short, database-fenced role leases in the shared <c>MonicaLeases</c> table.</summary>
/// <remarks>
/// PostgreSQL/GaussDB use native row locks and database time; verify the deployed server's concurrency contract.
/// SQLite uses serializable transactions and the supplied process clock for local/fixture use only.
/// Other providers fail explicitly. Every call owns and disposes a fresh context, with no connection retained by a grant.
/// </remarks>
/// <param name="contextFactory">Factory bound to the protected database and a model configured with ConfigureMonicaLeases.</param>
/// <param name="timeProvider">Clock for SQLite fixtures. Native providers always use their database clock.</param>
/// <param name="operationTimeout">Whole-call deadline, including lock waits and retry; defaults to five seconds.</param>
public sealed class RelationalLeaseStore<TContext>(IDbContextFactory<TContext> contextFactory,
    TimeProvider? timeProvider = null, TimeSpan? operationTimeout = null) : IExclusiveLeaseStore, ILeaseAdministration
    where TContext : DbContext
{
    private const int MAX_ATTEMPTS = 3;
    private const string STATEMENT_TICKS = "(CAST(EXTRACT(EPOCH FROM statement_timestamp()) * 10000000 AS bigint) + 621355968000000000)";
    private static readonly ConditionalWeakTable<IModel, TableBinding> TABLES = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _timeout = operationTimeout is null ? TimeSpan.FromSeconds(5)
        : operationTimeout > TimeSpan.Zero && operationTimeout <= TimeSpan.FromDays(1) ? operationTimeout.Value
        : throw new ArgumentOutOfRangeException(nameof(operationTimeout), "The operation timeout must be positive and at most one day.");

    /// <inheritdoc />
    public Task<LeaseGrant?> TryAcquireAsync(LeaseResource resource, string holderId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        Validate(resource, holderId, duration);
        return ExecuteAsync(async (db, token) =>
        {
            // Standbys do one indexed read without entering the holder's transaction/row-lock queue.
            if (await IsHeldAsync(db, resource, token)) return null;
            await using var transaction = await BeginTransactionAsync(db, token);
            var row = await LockAsync(db, resource, token);
            var now = await GetUtcNowAsync(db, token);
            if (row is not null && row.ExpiresAtUtcTicks > now.UtcTicks) return null;
            if (row is null)
            {
                row = new LeaseRecord { Scope = resource.Scope, Name = resource.Name };
                db.Add(row);
            }
            row.HolderId = holderId;
            row.TermId = Guid.NewGuid().ToString("N");
            row.AcquiredAtUtcTicks = now.UtcTicks;
            row.ConfirmedAtUtcTicks = now.UtcTicks;
            row.ExpiresAtUtcTicks = now.Add(duration).UtcTicks;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return row.ToGrant();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LeaseGrant?> TryRenewAsync(LeaseIdentity identity, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        Validate(identity.Resource, identity.HolderId, duration);
        return ExecuteAsync(async (db, token) =>
        {
            await using var transaction = await BeginTransactionAsync(db, token);
            var row = await LockAsync(db, identity.Resource, token);
            // Sample after the lock wait: a pre-lock timestamp can revive an expired lease.
            var now = await GetUtcNowAsync(db, token);
            if (!Matches(row, identity) || row!.ExpiresAtUtcTicks <= now.UtcTicks) return null;
            row.ConfirmedAtUtcTicks = now.UtcTicks;
            row.ExpiresAtUtcTicks = now.Add(duration).UtcTicks;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return row.ToGrant();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TryReleaseAsync(LeaseIdentity identity, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (db, token) => await db.Set<LeaseRecord>()
            .Where(row => row.Scope == identity.Resource.Scope && row.Name == identity.Resource.Name
                && row.HolderId == identity.HolderId && row.TermId == identity.TermId)
            .ExecuteDeleteAsync(token) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RevokeAsync(LeaseResource resource, CancellationToken cancellationToken = default) =>
        ExecuteAsync(async (db, token) => await db.Set<LeaseRecord>()
            .Where(row => row.Scope == resource.Scope && row.Name == resource.Name)
            .ExecuteDeleteAsync(token) == 1, cancellationToken);

    /// <summary>Locks and validates a grant in the caller's active protected transaction.</summary>
    /// <remarks>
    /// Call before locking business rows. The lock lasts until the caller commits or rolls back; this method never
    /// renews, commits or creates a context. Bound the transaction and do not hold it over external network effects.
    /// The supplied context must target the same lease authority and mapped table as the grant.
    /// </remarks>
    public async Task EnsureHeldAsync(DbContext context, LeaseIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Lease validation requires the protected operation's active transaction.");
        var row = await LockAsync(context, identity.Resource, cancellationToken);
        var now = await GetUtcNowAsync(context, cancellationToken);
        if (!Matches(row, identity) || row!.ExpiresAtUtcTicks <= now.UtcTicks) throw new LeaseLostException(identity);
        // The protected owner may save unrelated state; observation must not leave a stale tracked lease to rewrite.
        context.Entry(row).State = EntityState.Detached;
    }

    /// <inheritdoc />
    public Task<LeaseSnapshot> GetSnapshotAsync(LeaseResource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return ExecuteAsync(async (db, token) =>
        {
            var row = await db.Set<LeaseRecord>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.Scope == resource.Scope && row.Name == resource.Name, token);
            return new LeaseSnapshot(await GetUtcNowAsync(db, token), row is null ? [] : [row.ToObservation()]);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<LeaseSnapshot> GetSnapshotAsync(string scope, string namePrefix,
        CancellationToken cancellationToken = default) => ExecuteAsync(async (db, token) =>
        {
            var rows = await db.Set<LeaseRecord>().AsNoTracking()
                .Where(row => row.Scope == scope && row.Name.StartsWith(namePrefix))
                .OrderBy(row => row.Name).ToListAsync(token);
            return new LeaseSnapshot(await GetUtcNowAsync(db, token), rows.Select(row => row.ToObservation()).ToArray());
        }, cancellationToken);

    private async Task<TResult> ExecuteAsync<TResult>(Func<TContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var db = await contextFactory.CreateDbContextAsync(deadline.Token);
                _ = UsesNativeLocks(db);
                _ = Binding(db);
                db.Database.SetCommandTimeout(_timeout);
                return await operation(db, deadline.Token);
            }
            catch (Exception exception) when (attempt < MAX_ATTEMPTS && IsRetryable(exception))
            {
                // The failed context/transaction has already been disposed. Only definite conflicts are replayed.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 20) * attempt), deadline.Token);
            }
        }
    }

    private static Task<IDbContextTransaction> BeginTransactionAsync(DbContext db, CancellationToken token) =>
        db.Database.BeginTransactionAsync(UsesNativeLocks(db) ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, token);

    private async Task<bool> IsHeldAsync(DbContext db, LeaseResource resource, CancellationToken token)
    {
        if (UsesNativeLocks(db))
        {
            var sql = $"SELECT EXISTS (SELECT 1 FROM {Binding(db).SqlName} WHERE \"Scope\" = {{0}} AND \"Name\" = {{1}} "
                + $"AND \"ExpiresAtUtcTicks\" > {STATEMENT_TICKS}) AS \"Value\"";
            return await db.Database.SqlQueryRaw<bool>(sql, resource.Scope, resource.Name).SingleAsync(token);
        }
        var now = _time.GetUtcNow().UtcTicks;
        return await db.Set<LeaseRecord>().AsNoTracking().AnyAsync(row => row.Scope == resource.Scope
            && row.Name == resource.Name && row.ExpiresAtUtcTicks > now, token);
    }

    private static async Task<LeaseRecord?> LockAsync(DbContext db, LeaseResource resource, CancellationToken token)
    {
        if (!UsesNativeLocks(db)) return await db.Set<LeaseRecord>()
            .SingleOrDefaultAsync(row => row.Scope == resource.Scope && row.Name == resource.Name, token);
        var sql = $"SELECT * FROM {Binding(db).SqlName} WHERE \"Scope\" = {{0}} AND \"Name\" = {{1}} FOR UPDATE";
        return (await db.Set<LeaseRecord>().FromSqlRaw(sql, resource.Scope, resource.Name).ToListAsync(token)).SingleOrDefault();
    }

    private async Task<DateTimeOffset> GetUtcNowAsync(DbContext db, CancellationToken token)
    {
        if (!UsesNativeLocks(db)) return _time.GetUtcNow();
        var now = await db.Database.SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
            .SingleAsync(token);
        return new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    private static bool UsesNativeLocks(DbContext db)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Npgsql", StringComparison.Ordinal) || provider.Contains("Gauss", StringComparison.Ordinal)) return true;
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite") return false;
        throw new NotSupportedException($"Relational coordinator leases do not support provider '{provider}'.");
    }

    private static TableBinding Binding(DbContext db) => TABLES.GetValue(db.Model, _ =>
    {
        var entity = db.Model.FindEntityType(typeof(LeaseRecord))
            ?? throw new InvalidOperationException("ConfigureMonicaLeases must be applied to every participating context model.");
        return new TableBinding(db.GetService<ISqlGenerationHelper>().DelimitIdentifier(entity.GetTableName()!, entity.GetSchema()));
    });

    private static bool Matches(LeaseRecord? row, LeaseIdentity identity) =>
        row is not null && row.HolderId == identity.HolderId && row.TermId == identity.TermId;

    private static void Validate(LeaseResource resource, string holderId, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderId);
        if (holderId.Length > 256) throw new ArgumentOutOfRangeException(nameof(holderId));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
    }

    private static bool IsRetryable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var state = type.GetProperty("SqlState")?.GetValue(current) as string;
            if (state is "40001" or "40P01") return true;
            if (state == "23505" && exception is DbUpdateException update
                && update.Entries.Count > 0 && update.Entries.All(entry => entry.Entity is LeaseRecord && entry.State == EntityState.Added)
                && (type.GetProperty("ConstraintName")?.GetValue(current) as string == "PK_MonicaLeases"
                    || type.FullName == "GaussDB.PostgresException" && type.GetProperty("MessageText")?.GetValue(current) as string
                        == "duplicate key value violates unique constraint \"PK_MonicaLeases\"")) return true;
            if (type.GetProperty("SqliteErrorCode")?.GetValue(current) is int sqlite && sqlite is 5 or 6) return true;
        }
        return false;
    }

    private sealed record TableBinding(string SqlName);
}
