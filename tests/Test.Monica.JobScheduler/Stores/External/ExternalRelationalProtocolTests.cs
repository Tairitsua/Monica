using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Verifies the native SQL and driver primitives required by the scheduler protocol on actual server products.
/// </summary>
/// <remarks>
/// These are opt-in external integration checks. Every case owns its database and holds separate physical
/// connections; they do not establish scheduler-store correctness by themselves.
/// </remarks>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalRelationalProtocolTests
{
    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task SelectForUpdate_WhenFirstRowIsLocked_ShouldSkipItAndPreserveOrderAndLimit(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var database = await ExternalSchedulerDatabase.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(database, async () =>
        {
            await using var first = await database.OpenConnectionAsync(token);
            await using var second = await database.OpenConnectionAsync(token);
            Assert.NotEqual(await SessionIdAsync(first, token), await SessionIdAsync(second, token));
            await ExecuteAsync(first, null, """
                CREATE TABLE "SchedulerProtocolRows" ("Id" integer PRIMARY KEY);
                INSERT INTO "SchedulerProtocolRows" ("Id") VALUES (1), (2), (3), (4);
                """, token);

            await using var firstTransaction = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using var secondTransaction = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            Assert.Equal("read committed", await ScalarAsync(first, firstTransaction, "SHOW transaction_isolation", token));
            Assert.Equal("read committed", await ScalarAsync(second, secondTransaction, "SHOW transaction_isolation", token));
            var heldIds = await ReadIdsAsync(first, firstTransaction,
                "SELECT \"Id\" FROM \"SchedulerProtocolRows\" WHERE \"Id\" = 1 FOR UPDATE", token);
            Assert.Equal([1], heldIds);

            var secondIds = await ReadIdsAsync(second, secondTransaction, """
                SELECT "Id" FROM "SchedulerProtocolRows" ORDER BY "Id" LIMIT 2 FOR UPDATE SKIP LOCKED
                """, token);
            Assert.Equal([2, 3], secondIds);

            // Each transaction must skip locks held by the other physical session, but retain its own row lock.
            var firstIds = await ReadIdsAsync(first, firstTransaction, """
                SELECT "Id" FROM "SchedulerProtocolRows" ORDER BY "Id" LIMIT 2 FOR UPDATE SKIP LOCKED
                """, token);
            Assert.Equal([1, 4], firstIds);
            await secondTransaction.RollbackAsync(token);
            await firstTransaction.RollbackAsync(token);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task ConditionalUpdate_WhenTwoConnectionsReplaceOneToken_ShouldReturnExactlyOneWinner(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var database = await ExternalSchedulerDatabase.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(database, async () =>
        {
            await using var first = await database.OpenConnectionAsync(token);
            await using var second = await database.OpenConnectionAsync(token);
            Assert.NotEqual(await SessionIdAsync(first, token), await SessionIdAsync(second, token));
            await ExecuteAsync(first, null, """
                CREATE TABLE "SchedulerProtocolLeases" (
                    "Id" integer PRIMARY KEY,
                    "Token" varchar(32) NOT NULL,
                    "ExpiresAtUtc" timestamp with time zone NOT NULL);
                INSERT INTO "SchedulerProtocolLeases" ("Id", "Token", "ExpiresAtUtc")
                VALUES (1, 'initial', clock_timestamp() + interval '30 seconds');
                """, token);

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstUpdate = ReplaceTokenAsync(first, "first");
            var secondUpdate = ReplaceTokenAsync(second, "second");
            start.SetResult();
            var results = await Task.WhenAll(firstUpdate, secondUpdate);
            var winningToken = Assert.IsType<string>(Assert.Single(results, value => value is not null));
            Assert.Equal(winningToken,
                await ScalarAsync(first, null, "SELECT \"Token\" FROM \"SchedulerProtocolLeases\" WHERE \"Id\" = 1", token));

            await using (var renew = CreateCommand(first, null, """
                UPDATE "SchedulerProtocolLeases"
                SET "ExpiresAtUtc" = clock_timestamp() + interval '30 seconds'
                WHERE "Id" = 1 AND "Token" = @token AND "ExpiresAtUtc" > clock_timestamp()
                RETURNING "ExpiresAtUtc", clock_timestamp()
                """, ("token", winningToken)))
            await using (var reader = await renew.ExecuteReaderAsync(token))
            {
                Assert.True(await reader.ReadAsync(token));
                Assert.True(reader.GetDateTime(0) > reader.GetDateTime(1));
                Assert.False(await reader.ReadAsync(token));
            }

            await ExecuteAsync(first, null, """
                UPDATE "SchedulerProtocolLeases" SET "ExpiresAtUtc" = clock_timestamp() - interval '1 second'
                WHERE "Id" = 1
                """, token);
            await using var expiredRenew = CreateCommand(second, null, """
                UPDATE "SchedulerProtocolLeases"
                SET "ExpiresAtUtc" = clock_timestamp() + interval '30 seconds'
                WHERE "Id" = 1 AND "Token" = @token AND "ExpiresAtUtc" > clock_timestamp()
                RETURNING "Token"
                """, ("token", winningToken));
            Assert.Null(await expiredRenew.ExecuteScalarAsync(token));
            TestContext.Current.TestOutputHelper?.WriteLine("Conditional token replacement and expiry assertions passed before fixture cleanup.");

            async Task<object?> ReplaceTokenAsync(DbConnection connection, string replacement)
            {
                await start.Task.WaitAsync(token);
                await using var command = CreateCommand(connection, null, """
                    UPDATE "SchedulerProtocolLeases" SET "Token" = @replacement
                    WHERE "Id" = 1 AND "Token" = 'initial' AND "ExpiresAtUtc" > clock_timestamp()
                    RETURNING "Token"
                    """, ("replacement", replacement));
                return await command.ExecuteScalarAsync(token);
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task ExecuteAsync_WhenCancelledDuringObservedRowLockWait_ShouldStopAndAllowFreshWork(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var database = await ExternalSchedulerDatabase.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(database, async () =>
        {
            await using var holder = await database.OpenConnectionAsync(token);
            await using var waiter = await database.OpenConnectionAsync(token);
            await using var observer = await database.OpenConnectionAsync(token);
            var waiterId = await SessionIdAsync(waiter, token);
            Assert.NotEqual(await SessionIdAsync(holder, token), waiterId);
            await ExecuteAsync(holder, null, """
                CREATE TABLE "SchedulerProtocolRows" ("Id" integer PRIMARY KEY, "Value" integer NOT NULL);
                INSERT INTO "SchedulerProtocolRows" ("Id", "Value") VALUES (1, 0);
                """, token);

            await using var holderTransaction = await holder.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await ExecuteAsync(holder, holderTransaction,
                "UPDATE \"SchedulerProtocolRows\" SET \"Value\" = 1 WHERE \"Id\" = 1", token);
            await using var waiterTransaction = await waiter.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(10));
            await using var blocked = CreateCommand(waiter, waiterTransaction,
                "UPDATE \"SchedulerProtocolRows\" SET \"Value\" = 2 WHERE \"Id\" = 1");
            var pending = blocked.ExecuteNonQueryAsync(request.Token);
            try
            {
                await WaitForLockWaitAsync(observer, waiterId, pending, token);
                request.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => pending.WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            finally
            {
                request.Cancel();
                // Always observe the in-flight request before its command, transaction, or database is disposed.
                try
                {
                    await pending.WaitAsync(TimeSpan.FromSeconds(5), token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or DbException or TimeoutException)
                {
                }
            }

            if (waiter.State == ConnectionState.Open)
            {
                await waiterTransaction.RollbackAsync(token);
            }

            await holderTransaction.RollbackAsync(token);
            await observer.DisposeAsync();
            await using var fresh = await database.OpenConnectionAsync(token);
            Assert.Equal(1, await ExecuteAsync(fresh, null,
                "UPDATE \"SchedulerProtocolRows\" SET \"Value\" = 3 WHERE \"Id\" = 1", token));
            Assert.Equal(3, await ScalarAsync(fresh, null,
                "SELECT \"Value\" FROM \"SchedulerProtocolRows\" WHERE \"Id\" = 1", token));
        });
    }

    private static async Task WaitForLockWaitAsync(
        DbConnection observer,
        long waiterId,
        Task pending,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        using var observations = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var elapsed = Stopwatch.StartNew();
        var lastLocks = "no snapshot completed";
        try
        {
            do
            {
                var identityColumn = ExternalSchedulerSession.LockIdentityColumn(observer);
                await using var command = CreateCommand(observer, null, $"""
                    SELECT pid, {identityColumn} AS sessionid, locktype, database, relation, transactionid::text, mode, granted
                    FROM pg_catalog.pg_locks WHERE {identityColumn} = @session
                    """, ("session", waiterId));
                await using var reader = await command.ExecuteReaderAsync(budget.Token);
                var snapshot = new List<string>();
                var waiting = false;
                while (await reader.ReadAsync(budget.Token))
                {
                    snapshot.Add(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
                    waiting |= !reader.GetBoolean(7);
                }

                lastLocks = string.Join(";", snapshot);
                if (waiting)
                {
                    TestContext.Current.TestOutputHelper?.WriteLine(
                        $"Observed waiter {waiterId} after {elapsed.Elapsed.TotalMilliseconds:F0} ms: [{lastLocks}].");
                    return;
                }

                Assert.False(pending.IsCompleted, "The contender must still be waiting for the held row lock.");
            } while (await observations.WaitForNextTickAsync(budget.Token));
        }
        finally
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Lock observation for waiter {waiterId} ended after {elapsed.Elapsed.TotalMilliseconds:F0} ms; pending={pending.Status}; last locks=[{lastLocks}].");
        }

        throw new InvalidOperationException("No database lock wait was observed within the bounded probe.");
    }

    private static Task<long> SessionIdAsync(DbConnection connection, CancellationToken cancellationToken) =>
        ExternalSchedulerSession.ReadIdAsync(connection, cancellationToken);

    private static async Task<int[]> ReadIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetInt32(0));
        }

        return values.ToArray();
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
