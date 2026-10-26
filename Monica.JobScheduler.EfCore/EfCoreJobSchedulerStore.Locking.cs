using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models;
using System.Runtime.CompilerServices;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    // Eligibility hints use the start of their own statement, allowing an indexed time boundary without another
    // round trip. Mutations still sample clock_timestamp() after acquiring their authoritative row locks.
    private const string DATABASE_STATEMENT_TICKS_SQL =
        "(CAST(EXTRACT(EPOCH FROM statement_timestamp()) * 10000000 AS bigint) + 621355968000000000)";

    private async Task<bool> HasDueClaimCapacityAsync(
        JobSchedulerDbContext context, JobClaimRequest request, CancellationToken token)
    {
        var keys = request.JobKeys.ToArray();
        if (!UsesNativeRowLocks(context))
        {
            var ticks = ToTicks(await GetUtcNowAsync(context, token));
            return await context.ExecutionGates.AsNoTracking().AnyAsync(gate =>
                gate.SchedulerScopeKey == request.SchedulerScopeKey && gate.OwnerKey == request.OwnerKey
                && keys.Contains(gate.JobKey) && gate.ActiveCount < gate.MaxConcurrency
                && context.Executions.Any(row => row.SchedulerScopeKey == gate.SchedulerScopeKey
                    && row.OwnerKey == gate.OwnerKey && row.JobKey == gate.JobKey
                    && row.State == JobExecutionState.Queued && row.AvailableAtUtcTicks <= ticks), token);
        }

        var parameters = new List<object> { request.SchedulerScopeKey, request.OwnerKey };
        var placeholders = new List<string>();
        foreach (var key in keys)
        {
            placeholders.Add("{" + parameters.Count + "}");
            parameters.Add(key);
        }
        var sql = "SELECT EXISTS (SELECT 1 FROM \"JobExecutionGates\" g "
                  + "WHERE g.\"SchedulerScopeKey\" = {0} AND g.\"OwnerKey\" = {1} "
                  + "AND g.\"ActiveCount\" < g.\"MaxConcurrency\" AND g.\"JobKey\" IN (" + string.Join(",", placeholders) + ") "
                  + "AND EXISTS (SELECT 1 FROM \"JobExecutions\" e WHERE e.\"SchedulerScopeKey\" = g.\"SchedulerScopeKey\" "
                  + "AND e.\"OwnerKey\" = g.\"OwnerKey\" AND e.\"JobKey\" = g.\"JobKey\" "
                  + "AND e.\"State\" = 'Queued' AND e.\"AvailableAtUtcTicks\" <= "
                  + DATABASE_STATEMENT_TICKS_SQL + ")) AS \"Value\"";
        return await context.Database.SqlQueryRaw<bool>(sql, parameters.ToArray()).SingleAsync(token);
    }

    private static async Task<JobExecutionGateEntity?> LockClaimGateAsync(
        JobSchedulerDbContext context, JobClaimRequest request, string afterJob, IReadOnlySet<string> visited,
        DateTimeOffset observedAt, CancellationToken token)
    {
        var keys = request.JobKeys.Where(key => !visited.Contains(key)).ToArray();
        if (keys.Length == 0)
        {
            return null;
        }
        var ticks = ToTicks(observedAt);
        if (!UsesNativeRowLocks(context))
        {
            return await context.ExecutionGates
                .Where(gate => gate.SchedulerScopeKey == request.SchedulerScopeKey && gate.OwnerKey == request.OwnerKey
                               && keys.Contains(gate.JobKey) && gate.ActiveCount < gate.MaxConcurrency
                               && context.Executions.Any(row => row.SchedulerScopeKey == gate.SchedulerScopeKey
                                   && row.OwnerKey == gate.OwnerKey && row.JobKey == gate.JobKey
                                   && row.State == JobExecutionState.Queued && row.AvailableAtUtcTicks <= ticks))
                .OrderBy(gate => string.Compare(gate.JobKey, afterJob) > 0 ? 0 : 1).ThenBy(gate => gate.JobKey)
                .FirstOrDefaultAsync(token);
        }

        // Values, including job keys, are parameters. Eligibility and physical-lock skipping precede LIMIT;
        // sixty-four saturated or locked jobs therefore cannot hide the next runnable job.
        var parameters = new List<object> { request.SchedulerScopeKey, request.OwnerKey, ticks, afterJob };
        var placeholders = new List<string>();
        foreach (var key in keys)
        {
            placeholders.Add("{" + parameters.Count + "}");
            parameters.Add(key);
        }
        var sql = "SELECT g.* FROM \"JobExecutionGates\" g "
                  + "WHERE g.\"SchedulerScopeKey\" = {0} AND g.\"OwnerKey\" = {1} "
                  + "AND g.\"ActiveCount\" < g.\"MaxConcurrency\" AND g.\"JobKey\" IN (" + string.Join(",", placeholders) + ") "
                  + "AND EXISTS (SELECT 1 FROM \"JobExecutions\" e WHERE e.\"SchedulerScopeKey\" = g.\"SchedulerScopeKey\" "
                  + "AND e.\"OwnerKey\" = g.\"OwnerKey\" AND e.\"JobKey\" = g.\"JobKey\" "
                  + "AND e.\"State\" = 'Queued' AND e.\"AvailableAtUtcTicks\" <= {2}) "
                  + "ORDER BY CASE WHEN g.\"JobKey\" > {3} THEN 0 ELSE 1 END, g.\"JobKey\" LIMIT 1 FOR UPDATE SKIP LOCKED";
        return (await context.ExecutionGates.FromSqlRaw(sql, parameters.ToArray()).ToListAsync(token)).SingleOrDefault();
    }

    private static bool UsesNativeRowLocks(JobSchedulerDbContext context) =>
        context.Database.ProviderName is { } provider
        && (provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("Gauss", StringComparison.OrdinalIgnoreCase));

    private static async Task<JobExecutionGateEntity?> LockGateAsync(
        JobSchedulerDbContext context, string scope, string owner, string job,
        CancellationToken token, bool skipLocked = false)
    {
        if (!UsesNativeRowLocks(context))
        {
            return await context.ExecutionGates.SingleOrDefaultAsync(
                row => row.SchedulerScopeKey == scope && row.OwnerKey == owner && row.JobKey == job, token);
        }

        // Raw row locking is deliberately confined to PostgreSQL and the verified Vastbase/Gauss provider.
        // SQLite retains its serializable fixture boundary; capacity reads must never use SKIP LOCKED.
        var query = skipLocked
            ? context.ExecutionGates.FromSqlInterpolated($"SELECT * FROM \"JobExecutionGates\" WHERE \"SchedulerScopeKey\" = {scope} AND \"OwnerKey\" = {owner} AND \"JobKey\" = {job} FOR UPDATE SKIP LOCKED")
            : context.ExecutionGates.FromSqlInterpolated($"SELECT * FROM \"JobExecutionGates\" WHERE \"SchedulerScopeKey\" = {scope} AND \"OwnerKey\" = {owner} AND \"JobKey\" = {job} FOR UPDATE");
        return (await query.ToListAsync(token)).SingleOrDefault();
    }

    private static async Task<JobExecutionEntity?> LockExecutionAsync(
        JobSchedulerDbContext context, string scope, string instance, CancellationToken token)
    {
        if (!UsesNativeRowLocks(context))
        {
            return await context.Executions.SingleOrDefaultAsync(
                row => row.SchedulerScopeKey == scope && row.InstanceId == instance, token);
        }

        return (await context.Executions.FromSqlInterpolated(
                $"SELECT * FROM \"JobExecutions\" WHERE \"SchedulerScopeKey\" = {scope} AND \"InstanceId\" = {instance} FOR UPDATE")
            .ToListAsync(token)).SingleOrDefault();
    }

    private Task<TResult> WriteJobAsync<TResult>(string scope, string owner, string job,
        Func<JobSchedulerDbContext, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken,
        JobCoordinatorLeaseKey? coordinatorLease = null, [CallerMemberName] string operationName = "") =>
        WriteAsync(async (context, token) =>
        {
            await LockGateAsync(context, scope, owner, job, token);
            return await operation(context, token);
        }, cancellationToken, coordinatorLease, operationName);

    private Task<TResult> WriteExecutionAsync<TResult>(string scope, string instance,
        Func<JobSchedulerDbContext, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken,
        JobCoordinatorLeaseKey? coordinatorLease = null, [CallerMemberName] string operationName = "") =>
        WriteAsync(async (context, token) =>
        {
            // Immutable identity lookup takes no row lock. The authoritative state is reloaded only after Gate.
            var identity = await context.Executions.AsNoTracking()
                .Where(row => row.SchedulerScopeKey == scope && row.InstanceId == instance)
                .Select(row => new { row.OwnerKey, row.JobKey }).SingleOrDefaultAsync(token);
            if (identity is not null)
            {
                await LockGateAsync(context, scope, identity.OwnerKey, identity.JobKey, token);
                await LockExecutionAsync(context, scope, instance, token);
            }
            return await operation(context, token);
        }, cancellationToken, coordinatorLease, operationName);
}
