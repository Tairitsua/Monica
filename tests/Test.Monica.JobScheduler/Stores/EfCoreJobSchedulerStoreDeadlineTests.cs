using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Monica.JobScheduler.Models.Execution;
using Monica.Modules;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

/// <summary>
/// Verifies request deadlines without losing grants committed by earlier claim transactions.
/// </summary>
public sealed class EfCoreJobSchedulerStoreDeadlineTests
{
    [Fact]
    public async Task ClaimAsync_WhenLaterJobWaitsPastTheRequestDeadline_ShouldReturnPriorCommittedGrants()
    {
        var blocked = new LaterClaimTransaction();
        var now = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        await using var fixture = await StoreFixture.CreateEfCoreAsync(now,
            new ModuleJobSchedulerOption { StoreOperationTimeout = TimeSpan.FromSeconds(2) }, blocked);
        await fixture.SyncAsync(StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("first-job"), StoreFixture.TriggeredDeclaration("second-job"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "first-job", instanceId: "first-execution");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "second-job", instanceId: "second-execution");
        blocked.Armed = true;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(10));

        var claimed = await fixture.Store.ClaimAsync(new JobClaimRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE, OwnerKey = StoreFixture.OWNER_A,
            WorkerInstanceId = "deadline-worker", JobKeys = ["first-job", "second-job"], MaxCount = 2,
            LeaseDuration = TimeSpan.FromMinutes(1)
        }, caller.Token);

        blocked.TransactionAttempts.Should().Be(2);
        blocked.WaitCancelled.Should().BeTrue();
        caller.IsCancellationRequested.Should().BeFalse();
        var confirmed = claimed.Should().ContainSingle().Subject;
        confirmed.Execution.InstanceId.Should().Be("first-execution");
        var first = await fixture.Store.GetExecutionAsync(StoreFixture.SCOPE, "first-execution", caller.Token);
        first!.State.Should().Be(JobExecutionState.Running);
        first.LeaseExpiresAtUtc.Should().Be(confirmed.Execution.LeaseExpiresAtUtc);
        var second = await fixture.Store.GetExecutionAsync(StoreFixture.SCOPE, "second-execution", caller.Token);
        second!.State.Should().Be(JobExecutionState.Queued);
        second.LeaseExpiresAtUtc.Should().BeNull();
        blocked.Armed = false;
        (await fixture.Store.RenewLeaseAsync(confirmed.LeaseKey, TimeSpan.FromMinutes(1), caller.Token))
            .Status.Should().Be(JobLeaseRenewalStatus.Active);
        (await fixture.ClaimAsync(StoreFixture.OWNER_A, "next-worker", ["second-job"], maxCount: 1))
            .Should().ContainSingle().Which.Execution.InstanceId.Should().Be("second-execution");
    }

    private sealed class LaterClaimTransaction : DbTransactionInterceptor
    {
        internal bool Armed { get; set; }
        internal int TransactionAttempts { get; private set; }
        internal bool WaitCancelled { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            if (!Armed || ++TransactionAttempts != 2)
            {
                return result;
            }

            try
            {
                // Wait for the store's actual propagated request token, with no timer or sleep in the interceptor.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WaitCancelled = true;
                throw;
            }

            return result;
        }
    }
}
