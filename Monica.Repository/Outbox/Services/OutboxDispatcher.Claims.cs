using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

public sealed partial class OutboxDispatcher<TDbContext>
{
    private static readonly ConditionalWeakTable<IModel, string> CLAIM_SQL = new();
    private static readonly ConditionalWeakTable<IModel, string> SHARED_CLAIM_SQL = new();

    private async Task<List<OutboxMessage>> ClaimAsync(TDbContext db, long afterSequence, int count,
        string[]? eligibleTargets, CancellationToken cancellationToken)
    {
        var rows = db.Set<OutboxMessage>();
        var lease = Guid.NewGuid();
        if (UsesNativeClaims(db))
        {
            // One autocommitted statement locks only available rows and returns only the winning payloads.
            // Fully consume/dispose the reader before starting sends so the claim commit is confirmed.
            var sql = eligibleTargets is null ? CLAIM_SQL.GetValue(db.Model, _ => BuildClaimSql(db, false))
                : SHARED_CLAIM_SQL.GetValue(db.Model, _ => BuildClaimSql(db, true));
            object[] parameters = eligibleTargets is null
                ? [afterSequence, count, lease, registration.Options.DeliveryLease.TotalMilliseconds]
                : [afterSequence, count, lease, registration.Options.DeliveryLease.TotalMilliseconds, eligibleTargets];
            var claimed = await rows.FromSqlRaw(sql, parameters).AsNoTracking().ToListAsync(cancellationToken);
            claimed.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            return claimed;
        }

        // Portable fallback retains conditional claims and fetches payloads only after winning ownership.
        var now = time.GetUtcNow().UtcDateTime;
        var eligible = eligibleTargets is null ? rows.Where(x => x.DeliveryTarget == null)
            : rows.Where(x => x.DeliveryTarget != null && eligibleTargets.Contains(x.DeliveryTarget));
        var keys = await eligible.Where(x => x.Sequence > afterSequence && x.DeliveredAtUtc == null
                && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now)
                && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now))
            .OrderBy(x => x.Sequence).Select(x => x.Sequence).Take(count).ToListAsync(cancellationToken);
        foreach (var sequence in keys)
        {
            now = time.GetUtcNow().UtcDateTime;
            // Repository timestamps may have whole-second precision. Round up so storage cannot shorten a claim.
            var expiryTicks = now.Add(registration.Options.DeliveryLease).Ticks;
            var expiry = new DateTime(expiryTicks - expiryTicks % TimeSpan.TicksPerSecond + TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            await rows.Where(x => x.Sequence == sequence && x.DeliveredAtUtc == null
                    && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now)
                    && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= now))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.LeaseId, lease)
                    .SetProperty(x => x.LeaseUntilUtc, expiry)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken);
        }
        if (keys.Count == 0) return [];
        return await rows.AsNoTracking().Where(x => keys.Contains(x.Sequence) && x.LeaseId == lease)
            .OrderBy(x => x.Sequence).ToListAsync(cancellationToken);
    }

    private static bool UsesNativeClaims(TDbContext db)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        return provider.Contains("Npgsql", StringComparison.Ordinal) || provider.Contains("Gauss", StringComparison.Ordinal);
    }

    private static string BuildClaimSql(TDbContext db, bool shared)
    {
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        string Column(string name) => sql.DelimitIdentifier(entity.FindProperty(name)!.GetColumnName(table)!);
        var name = sql.DelimitIdentifier(table.Name, table.Schema);
        var sequence = Column(nameof(OutboxMessage.Sequence));
        var delivered = Column(nameof(OutboxMessage.DeliveredAtUtc));
        var retry = Column(nameof(OutboxMessage.NextAttemptAtUtc));
        var until = Column(nameof(OutboxMessage.LeaseUntilUtc));
        var token = Column(nameof(OutboxMessage.LeaseId));
        var attempts = Column(nameof(OutboxMessage.Attempts));
        var capability = shared ? $"AND {Column(nameof(OutboxMessage.DeliveryTarget))} = ANY ({{4}})"
            : $"AND {Column(nameof(OutboxMessage.DeliveryTarget))} IS NULL";
        return $$"""
            WITH due AS (
                SELECT {{sequence}} FROM {{name}}
                WHERE {{sequence}} > {0} AND {{delivered}} IS NULL
                  {{capability}}
                  AND ({{retry}} IS NULL OR {{retry}} <= (statement_timestamp() AT TIME ZONE 'UTC'))
                  AND ({{until}} IS NULL OR {{until}} <= (statement_timestamp() AT TIME ZONE 'UTC'))
                ORDER BY {{sequence}} LIMIT {1} FOR UPDATE SKIP LOCKED
            )
            UPDATE {{name}} AS message
            SET {{token}} = {2},
                {{until}} = date_trunc('second', (clock_timestamp() AT TIME ZONE 'UTC')
                    + CAST({3} AS double precision) * INTERVAL '1 millisecond') + INTERVAL '1 second',
                {{attempts}} = message.{{attempts}} + 1
            FROM due WHERE message.{{sequence}} = due.{{sequence}}
            RETURNING message.*
            """;
    }
}
