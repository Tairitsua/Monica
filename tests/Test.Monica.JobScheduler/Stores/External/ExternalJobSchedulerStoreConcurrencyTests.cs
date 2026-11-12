using System.Data;
using System.Data.Common;
using AwesomeAssertions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Exercises durable scheduler concurrency across distinct stores and real provider connections.
/// </summary>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalJobSchedulerStoreConcurrencyTests
{
    private const string SCOPE = "external-scope";
    private const string OWNER = "external-owner";
    private const string JOB = "external-job";

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task ClaimAsync_WhenIndependentStoresRace_ShouldRespectCapacityAndCommitGateWithRunningRows(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var first = fixture.CreateStore();
            var second = fixture.CreateStore();
            await ArrangeQueuedWorkAsync(first, 8, token);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstClaim = ClaimAfterStartAsync(first, "worker-one");
            var secondClaim = ClaimAfterStartAsync(second, "worker-two");
            start.SetResult();
            var results = await Task.WhenAll(firstClaim, secondClaim);
            var leases = results.SelectMany(result => result).ToArray();
            leases.Should().HaveCount(2);
            leases.Select(lease => lease.Execution.InstanceId).Should().OnlyHaveUniqueItems();
            await AssertGateMatchesRunningAsync(fixture.Database, 2, token);

            foreach (var lease in leases)
            {
                var committed = await second.GetExecutionAsync(SCOPE, lease.Execution.InstanceId, token);
                Assert.NotNull(committed);
                Assert.Equal(JobExecutionState.Running, committed.State);
            }

            async Task<IReadOnlyList<JobExecutionLease>> ClaimAfterStartAsync(IJobSchedulerStore store, string worker)
            {
                await start.Task.WaitAsync(token);
                return await store.ClaimAsync(ClaimRequest(worker), token);
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task CompleteAttemptAsync_WhenIndependentStoresRaceForSameLease_ShouldReleaseOneGateSlot(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var first = fixture.CreateStore();
            var second = fixture.CreateStore();
            await ArrangeQueuedWorkAsync(first, 3, token);
            var initial = await first.ClaimAsync(ClaimRequest("initial-worker"), token);
            initial.Should().HaveCount(2);
            var completion = new JobAttemptCompletion
            {
                LeaseKey = initial[0].LeaseKey,
                Outcome = JobAttemptOutcome.Succeeded
            };
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstCompletion = CompleteAfterStartAsync(first);
            var secondCompletion = CompleteAfterStartAsync(second);
            start.SetResult();
            var results = await Task.WhenAll(firstCompletion, secondCompletion);
            Assert.Single(results, result => result.Status == JobAttemptCompletionStatus.Applied);
            Assert.Single(results, result => result.Status == JobAttemptCompletionStatus.Lost);
            await AssertGateMatchesRunningAsync(fixture.Database, 1, token);

            var replacement = await second.ClaimAsync(ClaimRequest("replacement-worker"), token);
            Assert.Single(replacement);
            await AssertGateMatchesRunningAsync(fixture.Database, 2, token);
            var completed = await second.GetExecutionAsync(SCOPE, initial[0].Execution.InstanceId, token);
            Assert.NotNull(completed);
            Assert.Equal(JobExecutionState.Succeeded, completed.State);

            async Task<JobAttemptCompletionResult> CompleteAfterStartAsync(IJobSchedulerStore store)
            {
                await start.Task.WaitAsync(token);
                return await store.CompleteAttemptAsync(completion, token);
            }
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task ClaimAsync_WhenFirst64JobGatesAreSaturated_ShouldFindThe65thFreeJob(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var store = fixture.CreateStore();
            var jobKeys = Enumerable.Range(0, 65).Select(index => $"job-{index:D2}").ToArray();
            await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(SCOPE, OWNER,
                jobKeys.Select(job => Declaration(job, 1)).ToArray()), token);

            for (var index = 0; index < 64; index++)
            {
                await EnqueueAsync(store, jobKeys[index], $"running-{index:D2}", token);
            }

            var occupied = new List<JobExecutionLease>();
            // Arrange every occupied Gate with a bounded request. MaxCount is an upper bound, and a multi-job
            // request may return earlier committed grants when its overall operation deadline is exhausted.
            for (var index = 0; index < 64; index++)
            {
                occupied.AddRange(await store.ClaimAsync(new JobClaimRequest
                {
                    SchedulerScopeKey = SCOPE,
                    OwnerKey = OWNER,
                    WorkerInstanceId = "occupying-worker",
                    JobKeys = [jobKeys[index]],
                    MaxCount = 1,
                    LeaseDuration = TimeSpan.FromMinutes(5)
                }, token));
            }

            occupied.Should().HaveCount(64);
            for (var index = 0; index < 65; index++)
            {
                await EnqueueAsync(store, jobKeys[index], $"queued-{index:D2}", token);
            }

            var claimed = await fixture.CreateStore().ClaimAsync(new JobClaimRequest
            {
                SchedulerScopeKey = SCOPE,
                OwnerKey = OWNER,
                WorkerInstanceId = "free-worker",
                JobKeys = jobKeys,
                MaxCount = 1,
                LeaseDuration = TimeSpan.FromMinutes(2)
            }, token);
            var lease = Assert.Single(claimed);
            Assert.Equal(jobKeys[64], lease.Execution.Template.JobKey);
            Assert.Equal("queued-64", lease.Execution.InstanceId);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task RenewLeaseAsync_WhenCompletionCompetesForTheExecution_ShouldNotResurrectTerminalWork(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var first = fixture.CreateStore();
            var second = fixture.CreateStore();
            await ArrangeQueuedWorkAsync(first, 1, token);
            var lease = Assert.Single(await first.ClaimAsync(ClaimRequest("initial-worker"), token));
            var (completion, renewal) = await RaceUnderExecutionLockAsync(
                fixture.Database, lease.Execution.InstanceId,
                ct => first.CompleteAttemptAsync(new JobAttemptCompletion
                {
                    LeaseKey = lease.LeaseKey,
                    Outcome = JobAttemptOutcome.Succeeded
                }, ct),
                ct => second.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromMinutes(3), ct), token);

            Assert.Equal(JobAttemptCompletionStatus.Applied, completion.Status);
            Assert.True(renewal.Status is JobLeaseRenewalStatus.Active or JobLeaseRenewalStatus.Lost);
            var committed = await second.GetExecutionAsync(SCOPE, lease.Execution.InstanceId, token);
            Assert.NotNull(committed);
            Assert.Equal(JobExecutionState.Succeeded, committed.State);
            Assert.Null(committed.LeaseExpiresAtUtc);
            Assert.Equal(JobLeaseRenewalStatus.Lost,
                (await second.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromMinutes(3), token)).Status);
            await AssertGateMatchesRunningAsync(fixture.Database, 0, token);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task RenewLeaseAsync_WhenLeaseExpiresBehindRowLockAndRecoveryRuns_ShouldFenceTheOldToken(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var first = fixture.CreateStore();
            var second = fixture.CreateStore();
            await ArrangeQueuedWorkAsync(first, 1, token);
            var lease = Assert.Single(await first.ClaimAsync(ClaimRequest("initial-worker") with
            {
                LeaseDuration = TimeSpan.FromSeconds(2)
            }, token));
            var expiry = lease.Execution.LeaseExpiresAtUtc
                         ?? throw new InvalidOperationException("The claimed execution has no lease deadline.");

            await using (var blocker = await fixture.Database.OpenConnectionAsync(token))
            {
                var blockerSession = await ExternalSchedulerSession.ReadIdAsync(blocker, token);
                await using var transaction = await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
                await using (var heldRow = ExecutionCommand(blocker, transaction, """
                    SELECT "InstanceId" FROM "JobExecutions"
                    WHERE "SchedulerScopeKey" = @scope AND "InstanceId" = @instance FOR UPDATE
                    """, lease.Execution.InstanceId))
                {
                    Assert.Equal(lease.Execution.InstanceId, await heldRow.ExecuteScalarAsync(token));
                }

                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(TimeSpan.FromSeconds(10));
                var renewing = first.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromMinutes(3), budget.Token);
                Task<IReadOnlyList<JobExecutionInstance>>? recovering = null;
                try
                {
                    await WaitForStoreLockWaitAsync(blocker, transaction, blockerSession, 1, [renewing], token);
                    // Observe the actual grant expiring on the database clock while renewal is blocked. An UPDATE
                    // must not resurrect it using a WHERE predicate or time sampled before the lock wait.
                    await WaitForDatabaseDeadlineAsync(blocker, transaction, expiry, token);
                    await transaction.RollbackAsync(token);
                    recovering = second.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
                    {
                        SchedulerScopeKey = SCOPE,
                        MaxCount = 1
                    }, budget.Token);
                    Assert.Equal(JobLeaseRenewalStatus.Lost, (await renewing).Status);
                    Assert.Single(await recovering);
                }
                finally
                {
                    budget.Cancel();
                    await ObserveAsync(renewing);
                    if (recovering is not null)
                    {
                        await ObserveAsync(recovering);
                    }
                }
            }

            await AssertGateMatchesRunningAsync(fixture.Database, 0, token);
            var replacement = Assert.Single(await second.ClaimAsync(ClaimRequest("replacement-worker"), token));
            Assert.NotEqual(lease.LeaseKey.LeaseToken, replacement.LeaseKey.LeaseToken);
            Assert.Equal(JobAttemptCompletionStatus.Lost, (await first.CompleteAttemptAsync(new JobAttemptCompletion
            {
                LeaseKey = lease.LeaseKey,
                Outcome = JobAttemptOutcome.Succeeded
            }, token)).Status);
            Assert.Equal(JobLeaseMutationStatus.Lost, await first.AppendExecutionLogAsync(new JobExecutionLogEntry
            {
                LeaseKey = lease.LeaseKey,
                Message = "stale-worker-log"
            }, token));
            Assert.Equal(JobLeaseRenewalStatus.Active,
                (await second.RenewLeaseAsync(replacement.LeaseKey, TimeSpan.FromMinutes(3), token)).Status);
            await AssertGateMatchesRunningAsync(fixture.Database, 1, token);
        });
    }

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task AppendExecutionLogAsync_WhenRenewalCompetes_ShouldKeepTheDurableLogAndRenewedExpiry(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var first = fixture.CreateStore();
            var second = fixture.CreateStore();
            await ArrangeQueuedWorkAsync(first, 1, token);
            var lease = Assert.Single(await first.ClaimAsync(ClaimRequest("initial-worker"), token));
            const string message = "durable-log-racing-renewal";
            var (append, renewal) = await RaceUnderExecutionLockAsync(
                fixture.Database, lease.Execution.InstanceId,
                ct => first.AppendExecutionLogAsync(new JobExecutionLogEntry
                {
                    LeaseKey = lease.LeaseKey,
                    Message = message
                }, ct),
                ct => second.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromMinutes(3), ct), token);

            Assert.Equal(JobLeaseMutationStatus.Applied, append);
            Assert.Equal(JobLeaseRenewalStatus.Active, renewal.Status);
            var committed = await second.GetExecutionAsync(SCOPE, lease.Execution.InstanceId, token);
            Assert.NotNull(committed);
            Assert.Equal(JobExecutionState.Running, committed.State);
            Assert.Equal(renewal.LeaseExpiresAtUtc, committed.LeaseExpiresAtUtc);
            Assert.Single(committed.History,
                entry => entry.Kind == JobExecutionHistoryKind.ExecutionLog && entry.Message == message);
            await AssertGateMatchesRunningAsync(fixture.Database, 1, token);
        });
    }

    private static async Task ArrangeQueuedWorkAsync(
        IJobSchedulerStore store,
        int count,
        CancellationToken cancellationToken)
    {
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(SCOPE, OWNER, [Declaration(JOB, 2)]), cancellationToken);
        for (var index = 0; index < count; index++)
        {
            await EnqueueAsync(store, JOB, $"execution-{index}", cancellationToken);
        }
    }

    private static JobDeclaration Declaration(string jobKey, int maxConcurrency) => new()
    {
        JobKey = jobKey,
        JobName = jobKey,
        JobType = JobType.Triggered,
        JobArgsKey = "external-args",
        MaxConcurrency = maxConcurrency,
        MaxExecutionTimeout = TimeSpan.FromMinutes(5)
    };

    private static Task<JobExecutionInstance> EnqueueAsync(
        IJobSchedulerStore store, string jobKey, string instanceId, CancellationToken cancellationToken) =>
        store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = SCOPE,
            OwnerKey = OWNER,
            JobKey = jobKey,
            InstanceId = instanceId,
            JobArgs = "{}"
        }, cancellationToken);

    private static async Task<(TFirst First, TSecond Second)> RaceUnderExecutionLockAsync<TFirst, TSecond>(
        ExternalSchedulerDatabase database,
        string instanceId,
        Func<CancellationToken, Task<TFirst>> first,
        Func<CancellationToken, Task<TSecond>> second,
        CancellationToken cancellationToken)
    {
        await using var blocker = await database.OpenConnectionAsync(cancellationToken);
        var blockerSession = await ExternalSchedulerSession.ReadIdAsync(blocker, cancellationToken);
        await using var transaction = await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var command = ExecutionCommand(blocker, transaction, """
            SELECT "InstanceId" FROM "JobExecutions"
            WHERE "SchedulerScopeKey" = @scope AND "InstanceId" = @instance FOR UPDATE
            """, instanceId))
        {
            Assert.Equal(instanceId, await command.ExecuteScalarAsync(cancellationToken));
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        var firstTask = first(budget.Token);
        Task<TSecond>? secondTask = null;
        try
        {
            await WaitForStoreLockWaitAsync(blocker, transaction, blockerSession, 1, [firstTask], cancellationToken);
            secondTask = second(budget.Token);
            await WaitForStoreLockWaitAsync(blocker, transaction, blockerSession, 2, [firstTask, secondTask], cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return (await firstTask, await secondTask);
        }
        finally
        {
            budget.Cancel();
            await ObserveAsync(firstTask);
            if (secondTask is not null)
            {
                await ObserveAsync(secondTask);
            }
        }
    }

    private static async Task WaitForStoreLockWaitAsync(
        DbConnection blocker,
        DbTransaction transaction,
        long blockerSession,
        int minimumWaiters,
        Task[] pending,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        using var observations = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var command = blocker.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 5;
            var identityColumn = ExternalSchedulerSession.LockIdentityColumn(blocker);
            command.CommandText = $"""
                SELECT COUNT(DISTINCT waiting.{identityColumn}) FROM pg_locks AS waiting
                WHERE NOT waiting.granted AND waiting.{identityColumn} IN (
                    SELECT holding.{identityColumn} FROM pg_locks AS holding
                    WHERE holding.database = (SELECT oid FROM pg_database WHERE datname = current_database()))
                  AND (waiting.transactionid IN (
                        SELECT blocker.transactionid FROM pg_locks AS blocker
                        WHERE blocker.{identityColumn} = @blocker AND blocker.locktype = 'transactionid'
                          AND blocker.mode = 'ExclusiveLock' AND blocker.granted)
                    OR (waiting.locktype = 'tuple'
                        AND waiting.database = (SELECT oid FROM pg_database WHERE datname = current_database())
                        AND waiting.relation = '"JobExecutions"'::regclass))
                """;
            var sessionParameter = command.CreateParameter();
            sessionParameter.ParameterName = "blocker";
            sessionParameter.Value = blockerSession;
            command.Parameters.Add(sessionParameter);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(budget.Token)) >= minimumWaiters)
            {
                return;
            }

            Assert.All(pending, task => Assert.False(task.IsCompleted,
                "Both protocol operations must contend before the held execution lock is released."));
        } while (await observations.WaitForNextTickAsync(budget.Token));

        throw new InvalidOperationException("The expected store lock contention was not observed.");
    }

    private static async Task WaitForDatabaseDeadlineAsync(
        DbConnection blocker,
        DbTransaction transaction,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        using var observations = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await using var command = blocker.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 5;
            command.CommandText = "SELECT clock_timestamp() AT TIME ZONE 'UTC'";
            var databaseTime = Assert.IsType<DateTime>(await command.ExecuteScalarAsync(budget.Token));
            if (new DateTimeOffset(DateTime.SpecifyKind(databaseTime, DateTimeKind.Utc)) >= deadline)
            {
                return;
            }
        } while (await observations.WaitForNextTickAsync(budget.Token));

        throw new InvalidOperationException("The expected database lease deadline was not observed.");
    }

    private static DbCommand ExecutionCommand(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        string instanceId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = sql;
        foreach (var (name, value) in new[] { ("scope", SCOPE), ("instance", instanceId) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // The calling test observes results and asserts failures. Cleanup only drains pending operations.
        }
    }

    private static JobClaimRequest ClaimRequest(string worker) => new()
    {
        SchedulerScopeKey = SCOPE,
        OwnerKey = OWNER,
        WorkerInstanceId = worker,
        JobKeys = [JOB],
        MaxCount = 2,
        LeaseDuration = TimeSpan.FromMinutes(2)
    };

    private static async Task AssertGateMatchesRunningAsync(
        ExternalSchedulerDatabase database,
        int expected,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var gate = connection.CreateCommand();
        gate.CommandTimeout = 5;
        gate.CommandText = """
            SELECT "ActiveCount" FROM "JobExecutionGates"
            WHERE "SchedulerScopeKey" = 'external-scope'
              AND "OwnerKey" = 'external-owner' AND "JobKey" = 'external-job'
            """;
        Assert.Equal(expected, Convert.ToInt32(await gate.ExecuteScalarAsync(cancellationToken)));
        await using var running = connection.CreateCommand();
        running.CommandTimeout = 5;
        running.CommandText = """
            SELECT COUNT(*) FROM "JobExecutions"
            WHERE "SchedulerScopeKey" = 'external-scope'
              AND "OwnerKey" = 'external-owner' AND "JobKey" = 'external-job' AND "State" = 'Running'
            """;
        Assert.Equal(expected, Convert.ToInt32(await running.ExecuteScalarAsync(cancellationToken)));
    }
}
