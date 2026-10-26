using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.EfCore.Metrics;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.Modules;

namespace Monica.JobScheduler.EfCore;

/// <summary>
/// Persists the immutable job catalog and durable execution queue in one relational database.
/// </summary>
/// <remarks>
/// PostgreSQL and Vastbase mutations use short read-committed transactions and explicit Gate-before-Execution row
/// locks. SQLite retains a serializable fixture boundary. Every operation owns its context; no connection survives
/// into user job code. Renewal locks only its execution and never acquires a Gate.
/// </remarks>
public sealed partial class EfCoreJobSchedulerStore(
    IDbContextFactory<JobSchedulerDbContext> dbContextFactory,
    TimeProvider? timeProvider = null,
    IOptions<ModuleJobSchedulerOption>? options = null,
    IMeterFactory? meterFactory = null) : IJobSchedulerStore, IJobSchedulerStoreDescriptor, IDisposable
{
    private const int MAX_TRANSACTION_ATTEMPTS = 5;
    private static readonly JsonSerializerOptions JSON_OPTIONS = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<JobSchedulerDbContext> _dbContextFactory =
        dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly JobExecutionHistoryLimits _historyLimits = new(
        options?.Value.MaxExecutionHistoryEntriesPerExecution
        ?? JobExecutionHistoryLimits.DEFAULT_MAX_ENTRIES,
        options?.Value.MaxExecutionHistoryMessageLength
        ?? JobExecutionHistoryLimits.DEFAULT_MAX_MESSAGE_LENGTH);
    private string? _providerName;
    private readonly TimeSpan _operationTimeout = options?.Value.StoreOperationTimeout ?? TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _allOperations = new(options?.Value.MaxConcurrentStoreOperations ?? 8);
    private readonly SemaphoreSlim _ordinaryOperations = new((options?.Value.MaxConcurrentStoreOperations ?? 8)
        - (options?.Value.ReservedLeaseStoreOperations ?? 2));
    private readonly JobSchedulerStoreMetrics? _metrics = meterFactory is null ? null : new(meterFactory);

    /// <summary>Releases process-local admission resources after the host has stopped its scheduler services.</summary>
    public void Dispose()
    {
        _allOperations.Dispose();
        _ordinaryOperations.Dispose();
    }

    /// <inheritdoc />
    public string StoreKind => "EF Core";

    /// <inheritdoc />
    /// <remarks>The Entity Framework provider never changes at runtime, so the name is resolved once and cached.</remarks>
    public string? Provider => _providerName ??= ResolveProviderName();

    private string? ResolveProviderName()
    {
        using var dbContext = _dbContextFactory.CreateDbContext();
        return dbContext.Database.ProviderName;
    }

    private DateTimeOffset UtcNow => NormalizeUtc(_timeProvider.GetUtcNow());

    private async Task<DateTimeOffset> GetUtcNowAsync(
        JobSchedulerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName;
        if (providerName is not null
            && (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                || providerName.Contains("Postgre", StringComparison.OrdinalIgnoreCase)
                || providerName.Contains("Gauss", StringComparison.OrdinalIgnoreCase)))
        {
            var databaseTime = await dbContext.Database
                // CURRENT_TIMESTAMP is fixed at transaction start on PostgreSQL-family providers. Policy schedule
                // replacement needs wall-clock database time after any lock wait so its prospective cursor cannot
                // admit an occurrence that became historical while the transaction was waiting.
                .SqlQuery<DateTime>($"SELECT (clock_timestamp() AT TIME ZONE 'UTC') AS \"Value\"")
                .SingleAsync(cancellationToken);
            return new DateTimeOffset(DateTime.SpecifyKind(databaseTime, DateTimeKind.Utc));
        }

        return UtcNow;
    }

    private Task<TResult> ReadAsync<TResult>(
        Func<JobSchedulerDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken,
        [CallerMemberName] string operationName = "") =>
        RunStoreOperationAsync(operationName, false, async token =>
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(token);
            if (dbContext.Database.IsRelational()) dbContext.Database.SetCommandTimeout(_operationTimeout);
            return await operation(dbContext, token);
        }, cancellationToken);

    private Task<TResult> WriteAsync<TResult>(
        Func<JobSchedulerDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken, JobCoordinatorLeaseKey? coordinatorLease = null,
        [CallerMemberName] string operationName = "") =>
        RunStoreOperationAsync(operationName, operationName is nameof(RenewLeaseAsync) or nameof(CompleteAttemptAsync)
            or nameof(ReleaseLeaseAsync), token => WriteCoreAsync(operation, token, coordinatorLease), cancellationToken);

    private async Task<TResult> RunStoreOperationAsync<TResult>(string operationName, bool critical,
        Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_operationTimeout);
        var ordinaryEntered = false;
        var allEntered = false;
        try
        {
            if (!critical)
            {
                await _ordinaryOperations.WaitAsync(deadline.Token);
                ordinaryEntered = true;
            }
            await _allOperations.WaitAsync(deadline.Token);
            allEntered = true;
            var result = await operation(deadline.Token);
            outcome = "succeeded";
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "deadline";
            throw;
        }
        finally
        {
            if (allEntered) _allOperations.Release();
            if (ordinaryEntered) _ordinaryOperations.Release();
            _metrics?.Record(operationName, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<TResult> WriteCoreAsync<TResult>(
        Func<JobSchedulerDbContext, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken,
        JobCoordinatorLeaseKey? coordinatorLease = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            if (dbContext.Database.IsRelational()) dbContext.Database.SetCommandTimeout(_operationTimeout);
            await using var transaction = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(
                    UsesNativeRowLocks(dbContext) ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
                    cancellationToken)
                : null;
            try
            {
                await ValidateCoordinatorLeaseAsync(dbContext, coordinatorLease, cancellationToken);
                var result = await operation(dbContext, cancellationToken);
                await TrimExecutionHistoryAsync(dbContext, cancellationToken);
                SetMutationVersions(dbContext);
                await dbContext.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return result;
            }
            catch (Exception exception) when (attempt < MAX_TRANSACTION_ATTEMPTS && IsRetryable(exception))
            {
                _metrics?.Retry();
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25) * attempt), cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            var sqlState = type.GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState is "40001" or "40P01")
            {
                return true;
            }

            // Only known scheduler identity races are replayable. Arbitrary integrity/commit/connection errors
            // are surfaced to the caller rather than turning an unknown outcome into an automatic replay.
            if (sqlState == "23505")
            {
                var constraint = type.GetProperty("ConstraintName")?.GetValue(current) as string;
                if (string.IsNullOrEmpty(constraint) && type.FullName == "GaussDB.PostgresException"
                    && exception is DbUpdateException updateException)
                {
                    constraint = GetVastbaseIdentityConstraint(current, updateException);
                }

                if (constraint is "PK_JobExecutions" or "PK_JobExecutionGates" or "PK_JobSchedulerOwners"
                    or "PK_JobSchedulerCoordinators")
                {
                    return true;
                }
            }

            if (type.GetProperty("SqliteErrorCode")?.GetValue(current) is int errorCode
                && errorCode is 5 or 6)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetVastbaseIdentityConstraint(Exception exception, DbUpdateException updateException)
    {
        // Vastbase G100 Build15 can omit ConstraintName from its 23505 protocol response. Accept only its exact
        // primary-key message and a matching tracked insertion; unrelated integrity errors remain non-retryable.
        var message = exception.GetType().GetProperty("MessageText")?.GetValue(exception) as string;
        foreach (var entry in updateException.Entries.Where(entry => entry.State == EntityState.Added))
        {
            var constraint = entry.Entity switch
            {
                JobExecutionEntity => "PK_JobExecutions",
                JobExecutionGateEntity => "PK_JobExecutionGates",
                JobSchedulerOwnerEntity => "PK_JobSchedulerOwners",
                JobSchedulerCoordinatorEntity => "PK_JobSchedulerCoordinators",
                _ => null
            };
            if (constraint is not null
                && message == $"duplicate key value violates unique constraint \"{constraint}\"")
            {
                return constraint;
            }
        }

        return null;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JSON_OPTIONS);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JSON_OPTIONS)
        ?? throw new InvalidOperationException($"Stored scheduler payload '{typeof(T).Name}' was empty.");

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value) => value.ToUniversalTime();
    private static long ToTicks(DateTimeOffset value) => NormalizeUtc(value).UtcDateTime.Ticks;
    private static long? ToTicks(DateTimeOffset? value) => value is null ? null : ToTicks(value.Value);
    private static DateTimeOffset FromTicks(long value) => new(value, TimeSpan.Zero);
    private static DateTimeOffset? FromTicks(long? value) => value is null ? null : FromTicks(value.Value);
    private static string NewToken() => Guid.NewGuid().ToString("N");
    private static Guid NewVersion() => Guid.NewGuid();

    private static void SetMutationVersions(JobSchedulerDbContext dbContext)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var property = entry.Metadata.FindProperty("ConcurrencyToken");
            if (property?.ClrType == typeof(Guid))
            {
                entry.Property("ConcurrencyToken").CurrentValue = NewVersion();
            }
        }
    }

    private async Task TrimExecutionHistoryAsync(
        JobSchedulerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var pendingEntries = dbContext.ChangeTracker
            .Entries<JobExecutionHistoryEntity>()
            .Where(static entry => entry.State == EntityState.Added)
            .Select(static entry => entry.Entity)
            .ToArray();
        // Sequence numbers never repeat. Once a new sequence is appended, retaining only the final MaxEntries-wide
        // sequence window gives deterministic oldest-first eviction without counting or loading the history stream.
        var boundaries = pendingEntries
            .GroupBy(static entry => (entry.SchedulerScopeKey, entry.InstanceId))
            .Select(group => new
            {
                group.Key.SchedulerScopeKey,
                group.Key.InstanceId,
                EvictionSequenceBoundary = group.Max(static entry => entry.Sequence)
                                           - _historyLimits.MaxEntries
            })
            .Where(static boundary => boundary.EvictionSequenceBoundary >= 1)
            .ToArray();

        foreach (var boundary in boundaries)
        {
            await dbContext.ExecutionHistory
                .Where(entry => entry.SchedulerScopeKey == boundary.SchedulerScopeKey
                                && entry.InstanceId == boundary.InstanceId
                                && entry.Sequence <= boundary.EvictionSequenceBoundary)
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var pendingEntry in pendingEntries.Where(entry =>
                         entry.SchedulerScopeKey == boundary.SchedulerScopeKey
                         && entry.InstanceId == boundary.InstanceId
                         && entry.Sequence <= boundary.EvictionSequenceBoundary))
            {
                dbContext.Entry(pendingEntry).State = EntityState.Detached;
            }
        }
    }

    private static void ValidateIdentity(string value, string parameterName) =>
        JobSchedulerIdentity.ValidateStandard(value, parameterName);

    /// <summary>
    /// Builds an exact OR predicate over (OwnerKey, JobKey) identity pairs so a page mixing several owners and job
    /// keys never matches the Cartesian cross product of the two independent key sets. Both persisted entity families
    /// expose the same identity property names.
    /// </summary>
    private static Expression<Func<TEntity, bool>> CreateIdentityPredicate<TEntity>(
        IReadOnlyCollection<JobId> identities)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "item");
        Expression? body = null;
        foreach (var identity in identities)
        {
            var ownerMatch = Expression.Equal(
                Expression.Property(parameter, nameof(JobExecutionEntity.OwnerKey)),
                Expression.Constant(identity.OwnerKey));
            var jobMatch = Expression.Equal(
                Expression.Property(parameter, nameof(JobExecutionEntity.JobKey)),
                Expression.Constant(identity.JobKey));
            var identityMatch = Expression.AndAlso(ownerMatch, jobMatch);
            body = body is null ? identityMatch : Expression.OrElse(body, identityMatch);
        }

        return Expression.Lambda<Func<TEntity, bool>>(
            body ?? Expression.Constant(false),
            parameter);
    }

    /// <summary>
    /// Builds an exact OR predicate over (OwnerKey, JobKey, boundary) match rows so the second phase of a
    /// two-phase latest-per-group lookup fetches only the aggregated winners. Correlated "latest per group"
    /// subqueries are not translated reliably by every EF provider — GaussDB returned ungrouped rows, crashing
    /// keyed lookups — so winners are first reduced with plain GROUP BY aggregates and then matched by exact
    /// equality.
    /// </summary>
    private static Expression<Func<JobExecutionEntity, bool>> CreateBoundaryPredicate(
        IEnumerable<(JobId Identity, long Boundary)> boundaries,
        Expression<Func<JobExecutionEntity, long>> boundarySelector)
    {
        var parameter = Expression.Parameter(typeof(JobExecutionEntity), "item");
        var boundary = new ParameterRebinder(boundarySelector.Parameters[0], parameter)
            .Visit(boundarySelector.Body)!;
        Expression? body = null;
        foreach (var (identity, value) in boundaries)
        {
            var ownerMatch = Expression.Equal(
                Expression.Property(parameter, nameof(JobExecutionEntity.OwnerKey)),
                Expression.Constant(identity.OwnerKey));
            var jobMatch = Expression.Equal(
                Expression.Property(parameter, nameof(JobExecutionEntity.JobKey)),
                Expression.Constant(identity.JobKey));
            var boundaryMatch = Expression.Equal(boundary, Expression.Constant(value));
            var match = Expression.AndAlso(Expression.AndAlso(ownerMatch, jobMatch), boundaryMatch);
            body = body is null ? match : Expression.OrElse(body, match);
        }

        return Expression.Lambda<Func<JobExecutionEntity, bool>>(
            body ?? Expression.Constant(false),
            parameter);
    }

    /// <summary>
    /// Rebinds one lambda parameter onto another so a selector body composes into a larger predicate.
    /// </summary>
    private sealed class ParameterRebinder(ParameterExpression source, ParameterExpression target)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == source ? target : node;
    }
}
