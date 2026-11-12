using AwesomeAssertions;
using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Stores;

/// <summary>
/// Behavioral contracts every scheduler-store provider must satisfy. Each theory runs against the
/// in-memory provider and the SQLite-backed EF Core provider with an identical store clock.
/// </summary>
public sealed class JobSchedulerStoreContractTests
{
    private static readonly DateTimeOffset NOW = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOwnerSnapshot_ShouldUpsertDeclarationsPreservePolicyAndMarkMissingAbsent(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var first = await fixture.SyncAsync(StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));
        first.PresentCount.Should().Be(2);
        first.MarkedAbsentCount.Should().Be(0);

        var alpha = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);
        alpha.Should().NotBeNull();
        alpha!.IsPresent.Should().BeTrue();
        alpha.Policy.Overrides.HasAnyOverride.Should().BeFalse();

        var updated = await fixture.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            alpha.Policy,
            new JobPolicyOverrides { MaxConcurrencyOverride = 3 });

        // Republish without jobs.beta: the declaration disappears, but the policy of jobs.alpha survives.
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var second = await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha"));
        second.PresentCount.Should().Be(1);
        second.MarkedAbsentCount.Should().Be(1);

        var alphaAfter = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);
        alphaAfter!.Policy.ConcurrencyStamp.Should().Be(updated.ConcurrencyStamp);
        alphaAfter.Policy.Overrides.MaxConcurrencyOverride.Should().Be(3);
        alphaAfter.LastObservedAtUtc.Should().BeAfter(alpha.LastObservedAtUtc);

        var beta = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.beta", TestContext.Current.CancellationToken);
        beta!.IsPresent.Should().BeFalse();
        // The absent definition keeps its sticky policy for audit and a possible return.
        beta.Policy.Should().NotBeNull();

        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        var repeatedAbsent = await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha"));
        repeatedAbsent.MarkedAbsentCount.Should().Be(0);

        // A returning job resumes with its sticky policy intact.
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));
        var betaBack = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.beta", TestContext.Current.CancellationToken);
        betaBack!.IsPresent.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOwnerSnapshot_RepeatedIdenticalSnapshotsFromReplicasShouldBeIdempotent(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        var declarations = new[]
        {
            StoreFixture.RecurringDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta")
        };
        await fixture.SyncAsync(StoreFixture.OWNER_A, declarations);
        var observed = (await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken))!
            .LastObservedAtUtc;
        fixture.Time.Advance(TimeSpan.FromSeconds(30));
        var repeat = await fixture.SyncAsync(StoreFixture.OWNER_A, declarations);
        repeat.MarkedAbsentCount.Should().Be(0);
        var alpha = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);
        alpha!.IsPresent.Should().BeTrue();
        alpha.LastObservedAtUtc.Should().BeAfter(observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definitions_SameJobKeyAcrossOwnersShouldStayIndependent(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.shared"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.shared"));

        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.shared", instanceId: "a-1");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_B, "jobs.shared", instanceId: "b-1");

        var ownerAKeys = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.shared"]);
        ownerAKeys.Should().ContainSingle().Which.Execution.InstanceId.Should().Be("a-1");

        var ownerBKeys = await fixture.ClaimAsync(StoreFixture.OWNER_B, "worker-b", ["jobs.shared"]);
        ownerBKeys.Should().ContainSingle().Which.Execution.InstanceId.Should().Be("b-1");

        // One owner cannot claim the other owner's queued work.
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.shared", instanceId: "a-2");
        var wrongOwnerClaims = await fixture.ClaimAsync(StoreFixture.OWNER_B, "worker-b", ["jobs.shared"]);
        wrongOwnerClaims.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enqueue_ShouldRejectAbsentDisabledAndMismatchedAdmission(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.beta"),
            StoreFixture.RecurringDeclaration("jobs.removed"));

        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta"));
        Func<Task> act = () => fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.removed");
        await act.Should().ThrowAsync<JobDefinitionNotFoundException>();

        Func<Task> unknown = () => fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.unknown");
        await unknown.Should().ThrowAsync<JobDefinitionNotFoundException>();

        Func<Task> recurring = () => fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", jobArgs: null);
        await recurring.Should().ThrowAsync<ArgumentException>();

        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.beta", TestContext.Current.CancellationToken);
        await fixture.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.beta",
            definition!.Policy,
            new JobPolicyOverrides { DisabledOverride = true });
        Func<Task> disabled = () => fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta");
        await disabled.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enqueue_RepeatingIdenticalRequestShouldBeIdempotent(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta"));
        var first = await fixture.EnqueueTriggeredAsync(
            StoreFixture.OWNER_A,
            "jobs.beta",
            jobArgs: "{\"v\":1}",
            instanceId: "stable-id",
            availableAtUtc: NOW.AddMinutes(1));
        var second = await fixture.EnqueueTriggeredAsync(
            StoreFixture.OWNER_A,
            "jobs.beta",
            jobArgs: "{\"v\":1}",
            instanceId: "stable-id",
            availableAtUtc: NOW.AddMinutes(1));
        second.Should().Be(first);

        Func<Task> conflicting = () => fixture.EnqueueTriggeredAsync(
            StoreFixture.OWNER_A,
            "jobs.beta",
            jobArgs: "{\"v\":2}",
            instanceId: "stable-id");
        await conflicting.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunRecurringNow_ShouldQueueWithoutAdvancingCursor(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");
        var nextBefore = cursor.NextOccurrenceUtc;

        var paused = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);
        await fixture.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            paused!.Policy,
            new JobPolicyOverrides { DisabledOverride = true });

        // A paused schedule remains eligible for this explicit operator action.
        var execution = await fixture.RunRecurringNowAsync(StoreFixture.OWNER_A, "jobs.alpha");
        execution.State.Should().Be(JobExecutionState.Queued);
        execution.Origin.Should().Be(JobExecutionOrigin.RecurringRunNow);
        execution.RecurringOccurrenceUtc.Should().BeNull();

        var operational = await fixture.Store.GetOperationalSummaryAsync(
            fixture.Scope,
            new JobId(StoreFixture.OWNER_A, "jobs.alpha"), TestContext.Current.CancellationToken);
        operational!.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Suspended);
        operational.LatestExecution!.InstanceId.Should().Be(execution.InstanceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryOperationalSummaries_MultipleExecutionsPerJobShouldReportLatest(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.alpha"),
            StoreFixture.TriggeredDeclaration("jobs.beta"));

        // An execution history behind one job must reduce to a single latest row per summary, exactly as the
        // GaussDB incident showed: several executions per job crashed the page's keyed lookup.
        for (var i = 0; i < 3; i++)
        {
            await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: $"exec-{i}");
            fixture.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var page = await fixture.Store.QueryOperationalSummariesAsync(
            fixture.Scope,
            new JobDefinitionQuery { PageNumber = 1, PageSize = 10 },
            TestContext.Current.CancellationToken);
        var beta = page.Items.Should().ContainSingle(
            summary => summary.Definition.Id == new JobId(StoreFixture.OWNER_A, "jobs.beta")).Subject;
        beta.LatestExecution!.InstanceId.Should().Be("exec-2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_ShouldEnforceConcurrencyGateAndLocalJobKeys(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta", maxConcurrency: 2));
        for (var i = 0; i < 4; i++)
        {
            await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: $"exec-{i}");
        }

        // The worker does not know jobs.beta: nothing is claimable.
        var unknownKey = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.other"]);
        unknownKey.Should().BeEmpty();

        var firstBatch = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"], maxCount: 8);
        firstBatch.Should().HaveCount(2);
        firstBatch.Should().OnlyContain(lease => lease.Execution.State == JobExecutionState.Running);

        // The gate holds the configured capacity of two across workers.
        var secondBatch = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-b", ["jobs.beta"], maxCount: 8);
        secondBatch.Should().BeEmpty();

        await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = firstBatch[0].LeaseKey,
            Outcome = JobAttemptOutcome.Succeeded
        }, TestContext.Current.CancellationToken);
        var thirdBatch = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-b", ["jobs.beta"], maxCount: 8);
        thirdBatch.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteAttempt_ShouldApplyRetryPolicyAndTerminalStates(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta"));
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.beta", TestContext.Current.CancellationToken);
        await fixture.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.beta",
            definition!.Policy,
            new JobPolicyOverrides { RetryCountOverride = 1 });

        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "retry-exec");
        var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Single();

        var firstFailure = await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey,
            Outcome = JobAttemptOutcome.Failed,
            RetryDelay = TimeSpan.FromSeconds(5)
        }, TestContext.Current.CancellationToken);
        firstFailure.Status.Should().Be(JobAttemptCompletionStatus.Applied);
        firstFailure.Execution!.State.Should().Be(JobExecutionState.Queued);
        firstFailure.Execution.RetryAttempt.Should().Be(1);
        firstFailure.Execution.AvailableAtUtc.Should().Be(NOW.AddSeconds(5));

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        var retryLease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Single();
        retryLease.Execution.InstanceId.Should().Be("retry-exec");
        var exhausted = await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = retryLease.LeaseKey,
            Outcome = JobAttemptOutcome.Failed
        }, TestContext.Current.CancellationToken);
        exhausted.Execution!.State.Should().Be(JobExecutionState.Failed);
        exhausted.Execution.CompletedAtUtc.Should().NotBeNull();

        // A stale lease completion is fenced.
        var stale = await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey,
            Outcome = JobAttemptOutcome.Succeeded
        }, TestContext.Current.CancellationToken);
        stale.Status.Should().Be(JobAttemptCompletionStatus.Lost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_ShouldCancelQueuedImmediatelyAndRunningCooperatively(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "queued-exec");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "running-exec");

        var queued = await fixture.Store.RequestCancellationAsync(fixture.Scope, "queued-exec", cancellationToken: TestContext.Current.CancellationToken);
        queued.Status.Should().Be(JobCancellationStatus.Cancelled);
        (await fixture.Store.GetExecutionAsync(fixture.Scope, "queued-exec", TestContext.Current.CancellationToken))!.State
            .Should().Be(JobExecutionState.Cancelled);

        var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Single();
        var running = await fixture.Store.RequestCancellationAsync(fixture.Scope, "running-exec", cancellationToken: TestContext.Current.CancellationToken);
        running.Status.Should().Be(JobCancellationStatus.CancellationRequested);
        var renewal = await fixture.Store.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        renewal.Status.Should().Be(JobLeaseRenewalStatus.CancellationRequested);

        // A completion after a persisted cancellation always resolves to Cancelled.
        var completion = await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
        {
            LeaseKey = lease.LeaseKey,
            Outcome = JobAttemptOutcome.Succeeded
        }, TestContext.Current.CancellationToken);
        completion.Execution!.State.Should().Be(JobExecutionState.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseRecovery_ShouldRequeueOrCancelAndFenceLostLeases(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta", maxConcurrency: 2));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "recover-exec");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "cancel-exec");

        var claimedLeases = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"]);
        var recoverLease = claimedLeases.Single(lease => lease.Execution.InstanceId == "recover-exec");
        var cancelLease = claimedLeases.Single(lease => lease.Execution.InstanceId == "cancel-exec");
        await fixture.Store.RequestCancellationAsync(fixture.Scope, "cancel-exec", cancellationToken: TestContext.Current.CancellationToken);

        fixture.Time.Advance(TimeSpan.FromSeconds(31));
        var recovered = await fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            MaxCount = 10
        }, TestContext.Current.CancellationToken);
        recovered.Should().HaveCount(2);

        var recoveredExecution = (await fixture.Store.GetExecutionAsync(fixture.Scope, "recover-exec", TestContext.Current.CancellationToken))!;
        recoveredExecution.State.Should().Be(JobExecutionState.Queued);
        recoveredExecution.LeaseLossCount.Should().Be(1);

        var cancelledExecution = (await fixture.Store.GetExecutionAsync(fixture.Scope, "cancel-exec", TestContext.Current.CancellationToken))!;
        cancelledExecution.State.Should().Be(JobExecutionState.Cancelled);

        // The recovered capacity is claimable again, and the expired lease is fenced.
        (await fixture.Store.RenewLeaseAsync(recoverLease.LeaseKey, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status
            .Should().Be(JobLeaseRenewalStatus.Lost);
        var reclaimed = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"]);
        reclaimed.Should().ContainSingle().Which.Execution.InstanceId.Should().Be("recover-exec");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseRecovery_ShouldFailExpiredAttemptThatExceededItsTimeout(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "timeout-exec");
        var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Single();

        // The declaration's timeout is five minutes; the expired lease is discovered well past it.
        fixture.Time.Advance(TimeSpan.FromMinutes(6));
        var recovered = await fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            MaxCount = 10
        }, TestContext.Current.CancellationToken);
        recovered.Should().ContainSingle();

        var execution = (await fixture.Store.GetExecutionAsync(fixture.Scope, "timeout-exec", TestContext.Current.CancellationToken))!;
        execution.State.Should().Be(JobExecutionState.Failed);
        execution.RetryAttempt.Should().Be(1);
        execution.LeaseLossCount.Should().Be(1);
        execution.CompletedAtUtc.Should().NotBeNull();
        execution.History.Should().Contain(entry =>
            entry.NewState == JobExecutionState.Failed
            && entry.Message.Contains("MaxExecutionTimeout"));

        // The terminal outcome fences the expired lease instead of requeueing it for another attempt.
        (await fixture.Store.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status
            .Should().Be(JobLeaseRenewalStatus.Lost);
        (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Should().BeEmpty();

        // No running work remains, so a further recovery pass reports nothing.
        (await fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            MaxCount = 10
        }, TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseRecovery_ShouldConsumeRetryBudgetWhenExpiredAttemptExceededItsTimeout(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.beta") with { RetryCount = 1 });
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "timeout-retry-exec");
        await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"]);

        fixture.Time.Advance(TimeSpan.FromMinutes(6));
        await fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            MaxCount = 10,
            RetryDelay = TimeSpan.FromSeconds(5)
        }, TestContext.Current.CancellationToken);

        var retried = (await fixture.Store.GetExecutionAsync(fixture.Scope, "timeout-retry-exec", TestContext.Current.CancellationToken))!;
        retried.State.Should().Be(JobExecutionState.Queued);
        retried.RetryAttempt.Should().Be(1);
        retried.AvailableAtUtc.Should().Be(NOW.AddMinutes(6).AddSeconds(5));

        // The retry attempt claims fresh timeout budget, exceeds it, and exhausts the one-retry policy.
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Single();
        fixture.Time.Advance(TimeSpan.FromMinutes(6));
        await fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = fixture.Scope,
            MaxCount = 10
        }, TestContext.Current.CancellationToken);

        var exhausted = (await fixture.Store.GetExecutionAsync(fixture.Scope, "timeout-retry-exec", TestContext.Current.CancellationToken))!;
        exhausted.State.Should().Be(JobExecutionState.Failed);
        exhausted.RetryAttempt.Should().Be(2);
        exhausted.CompletedAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseRecovery_ShouldFailExecutionThatExhaustedItsLeaseLossBudget(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        // A one-hour timeout keeps every recovery well inside the attempt's execution budget.
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.beta") with { MaxExecutionTimeout = TimeSpan.FromHours(1) });
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "loss-budget-exec");

        for (var loss = 1; loss <= 3; loss++)
        {
            await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"]);
            fixture.Time.Advance(TimeSpan.FromSeconds(31));
            await fixture.Store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
            {
                SchedulerScopeKey = fixture.Scope,
                MaxCount = 10,
                MaxLeaseLossesBeforeFailure = 3
            }, TestContext.Current.CancellationToken);

            var execution = (await fixture.Store.GetExecutionAsync(fixture.Scope, "loss-budget-exec", TestContext.Current.CancellationToken))!;
            execution.LeaseLossCount.Should().Be(loss);
            if (loss < 3)
            {
                // Benign lease losses inside the budget still requeue without consuming retry policy.
                execution.State.Should().Be(JobExecutionState.Queued);
                execution.RetryAttempt.Should().Be(0);
            }
            else
            {
                execution.State.Should().Be(JobExecutionState.Failed);
                execution.CompletedAtUtc.Should().NotBeNull();
                execution.History.Should().Contain(entry =>
                    entry.NewState == JobExecutionState.Failed
                    && entry.Message.Contains("lost leases"));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecurringSynchronization_ShouldCreateProspectivelyAndRemoveStaleCursors(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");
        cursor.Version.Should().Be(1);
        cursor.NextOccurrenceUtc.Should().Be(NOW.AddHours(1));
        cursor.IsSuspended.Should().BeFalse();

        // Debug mode suspends without losing the schedule.
        var suspended = await fixture.Store.SynchronizeRecurringScheduleAsync(
            new RecurringScheduleSynchronization
            {
                CursorKey = cursor.Key,
                HostSuspensionReasons = JobRecurringScheduleSuspensionReason.Manual
            }, TestContext.Current.CancellationToken);
        suspended.Status.Should().Be(RecurringScheduleSynchronizationStatus.Updated);
        suspended.Cursor!.IsSuspended.Should().BeTrue();
        suspended.Cursor.NextOccurrenceUtc.Should().BeNull();

        // A resume is prospective: it never replays suppressed time.
        fixture.Time.Advance(TimeSpan.FromHours(2));
        var resumed = await fixture.Store.SynchronizeRecurringScheduleAsync(
            new RecurringScheduleSynchronization { CursorKey = cursor.Key }, TestContext.Current.CancellationToken);
        resumed.Cursor!.IsSuspended.Should().BeFalse();
        resumed.Cursor.NextOccurrenceUtc.Should().Be(NOW.AddHours(3));

        // Replacing the recurring declaration with a triggered one removes its cursor during owner snapshot sync.
        fixture.Time.Advance(TimeSpan.FromHours(2));
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);
        var triggered = definition!.Declaration with { JobType = JobType.Triggered, CronExpression = null, JobArgsKey = "jobs.alphaArgs" };
        await fixture.SyncAsync(StoreFixture.OWNER_A, triggered);
        var dueAfterRemoval = await fixture.Store.GetDueRecurringSchedulesAsync(fixture.Scope, StoreFixture.OWNER_A, 10, TestContext.Current.CancellationToken);
        dueAfterRemoval.Should().BeEmpty();

        var removed = await fixture.Store.SynchronizeRecurringScheduleAsync(
            new RecurringScheduleSynchronization { CursorKey = cursor.Key }, TestContext.Current.CancellationToken);
        removed.Status.Should().Be(RecurringScheduleSynchronizationStatus.DefinitionNotRecurring);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncOwnerSnapshot_RemovingRecurringJobShouldRemoveItsCursor(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha"));
        await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");

        await fixture.SyncAsync(StoreFixture.OWNER_A);
        fixture.Time.Advance(TimeSpan.FromHours(1));

        var due = await fixture.Store.GetDueRecurringSchedulesAsync(
            fixture.Scope,
            StoreFixture.OWNER_A,
            10,
            TestContext.Current.CancellationToken);
        due.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecurringMaterialization_ShouldMaterializeOnceAndAdvanceCursor(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");

        fixture.Time.Advance(TimeSpan.FromHours(1));
        var due = await fixture.Store.GetDueRecurringSchedulesAsync(fixture.Scope, StoreFixture.OWNER_A, 10, TestContext.Current.CancellationToken);
        due.Should().ContainSingle().Which.NextOccurrenceUtc.Should().Be(NOW.AddHours(1));

        var occurrence = NOW.AddHours(1);
        var materialization = new RecurringOccurrenceMaterialization
        {
            CursorKey = cursor.Key,
            ExpectedVersion = cursor.Version,
            ExpectedOccurrenceUtc = occurrence,
            InstanceId = "alpha-occurrence-1"
        };
        var result = await fixture.Store.TryMaterializeRecurringOccurrenceAsync(materialization, TestContext.Current.CancellationToken);
        result.Status.Should().Be(RecurringMaterializationStatus.Materialized);
        result.Execution!.State.Should().Be(JobExecutionState.Queued);
        result.Execution.RecurringOccurrenceUtc.Should().Be(occurrence);
        result.Execution.Template.JobType.Should().Be(JobType.Recurring);
        result.Cursor!.NextOccurrenceUtc.Should().Be(occurrence.AddHours(1));

        // A repeated attempt with the stale cursor is rejected.
        var stale = await fixture.Store.TryMaterializeRecurringOccurrenceAsync(materialization, TestContext.Current.CancellationToken);
        stale.Status.Should().Be(RecurringMaterializationStatus.StaleCursor);

        // The materialized occurrence is claimable and idempotent by instance identity.
        var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.alpha"])).Single();
        lease.Execution.InstanceId.Should().Be("alpha-occurrence-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecurringMaterialization_ShouldAdvanceFromTheStoreClockAfterAnOutage(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration(
            "jobs.alpha",
            cron: "0 0 * * * *"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");

        // The first hourly occurrence is overdue, while two later occurrences have already elapsed. The store must
        // coalesce that backlog using its authoritative clock and leave the cursor at the first future occurrence.
        fixture.Time.Advance(TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(30)));
        var result = await fixture.Store.TryMaterializeRecurringOccurrenceAsync(new RecurringOccurrenceMaterialization
        {
            CursorKey = cursor.Key,
            ExpectedVersion = cursor.Version,
            ExpectedOccurrenceUtc = NOW.AddHours(1),
            InstanceId = "alpha-outage-occurrence"
        }, TestContext.Current.CancellationToken);

        result.Status.Should().Be(RecurringMaterializationStatus.Materialized);
        result.Cursor!.NextOccurrenceUtc.Should().Be(NOW.AddHours(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecurringMaterialization_ShouldRejectAStaleCursorAfterDeclarationPublication(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration(
            "jobs.alpha",
            cron: "0 0 * * * *"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");

        fixture.Time.Advance(TimeSpan.FromHours(1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration(
            "jobs.alpha",
            cron: "0 30 * * * *",
            retryCount: 3));

        // Definition publication and cursor synchronization are intentionally separate. The old cursor must not admit
        // an occurrence with the old schedule or execution template during that synchronization window.
        var stale = await fixture.Store.TryMaterializeRecurringOccurrenceAsync(new RecurringOccurrenceMaterialization
        {
            CursorKey = cursor.Key,
            ExpectedVersion = cursor.Version,
            ExpectedOccurrenceUtc = NOW.AddHours(1),
            InstanceId = "stale-declaration-occurrence"
        }, TestContext.Current.CancellationToken);

        stale.Status.Should().Be(RecurringMaterializationStatus.StaleCursor);
        stale.Execution.Should().BeNull();
        stale.Cursor!.Schedule.CronExpression.Should().Be("0 0 * * * *");

        var executions = await fixture.Store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.alpha"
        }, TestContext.Current.CancellationToken);
        executions.TotalCount.Should().Be(0);

        var repaired = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");
        repaired.Template.RetryCount.Should().Be(3);
        repaired.Schedule.CronExpression.Should().Be("0 30 * * * *");
        repaired.NextOccurrenceUtc.Should().Be(NOW.AddHours(1).AddMinutes(30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecurringMaterialization_OverdueCapacityShouldRecordSkippedOccurrence(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *", maxConcurrency: 1));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");

        fixture.Time.Advance(TimeSpan.FromHours(1));
        var first = await MaterializeDueAsync(fixture, cursor.Key);
        first.Status.Should().Be(RecurringMaterializationStatus.Materialized);
        first.Execution!.State.Should().Be(JobExecutionState.Queued);

        fixture.Time.Advance(TimeSpan.FromHours(1));
        var second = await MaterializeDueAsync(fixture, cursor.Key);
        second.Status.Should().Be(RecurringMaterializationStatus.Materialized);
        second.Execution!.State.Should().Be(JobExecutionState.Skipped);
        second.Execution.SkipReason.Should().Be(JobExecutionSkipReason.RecurringCapacityUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdatePolicy_ShouldApplyConcurrencyAndScheduleToCursorWithOptimisticFencing(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"));
        var cursor = await fixture.SyncCursorAsync(StoreFixture.OWNER_A, "jobs.alpha");
        var definition = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);

        // Optimistic concurrency fencing.
        Func<Task> stale = () => fixture.Store.UpdatePolicyAsync(
            fixture.Scope,
            StoreFixture.OWNER_A,
            "jobs.alpha",
            new JobPolicyChange
            {
                Overrides = new JobPolicyOverrides { RetryCountOverride = 2 },
                ExpectedConcurrencyStamp = "00000000000000000000000000000000"
            });
        await stale.Should().ThrowAsync<JobPolicyConcurrencyException>();

        Func<Task> unknown = () => fixture.Store.UpdatePolicyAsync(
            fixture.Scope,
            StoreFixture.OWNER_A,
            "jobs.missing",
            new JobPolicyChange
            {
                Overrides = new JobPolicyOverrides(),
                ExpectedConcurrencyStamp = definition!.Policy.ConcurrencyStamp
            });
        await unknown.Should().ThrowAsync<JobDefinitionNotFoundException>();

        // A schedule replacement is prospective from the store time.
        fixture.Time.Advance(TimeSpan.FromHours(2));
        await fixture.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            definition!.Policy,
            new JobPolicyOverrides
            {
                ScheduleOverride = new JobScheduleOverride { CronExpression = "0 0 0 * * *" }
            });
        var replaced = await fixture.Store.GetOperationalSummaryAsync(
            fixture.Scope,
            new JobId(StoreFixture.OWNER_A, "jobs.alpha"), TestContext.Current.CancellationToken);
        replaced!.NextOccurrenceUtc.Should().Be(new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));

        // The concurrency gate follows the effective policy.
        var withConcurrency = await fixture.Store.GetDefinitionAsync(fixture.Scope, StoreFixture.OWNER_A, "jobs.alpha", TestContext.Current.CancellationToken);
        await fixture.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            withConcurrency!.Policy,
            new JobPolicyOverrides
            {
                ScheduleOverride = new JobScheduleOverride { CronExpression = "0 0 0 * * *" },
                MaxConcurrencyOverride = 5
            });
        for (var i = 0; i < 5; i++)
        {
            await fixture.RunRecurringNowAsync(StoreFixture.OWNER_A, "jobs.alpha", instanceId: $"gate-{i}");
        }
        var fullClaims = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.alpha"], maxCount: 10);
        fullClaims.Should().HaveCount(5);
        var overCapacity = await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-b", ["jobs.alpha"], maxCount: 10);
        overCapacity.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitionQueries_ShouldFilterSortAndPageServerSide(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.zulu") with { Description = "description-search-needle" },
            StoreFixture.TriggeredDeclaration("jobs.alpha"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.RecurringDeclaration("jobs.alpha"));

        var ownerBPage = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { OwnerKey = StoreFixture.OWNER_B }, TestContext.Current.CancellationToken);
        ownerBPage.TotalCount.Should().Be(1);
        ownerBPage.Items.Single().OwnerKey.Should().Be(StoreFixture.OWNER_B);

        var triggeredPage = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { JobType = JobType.Triggered }, TestContext.Current.CancellationToken);
        triggeredPage.TotalCount.Should().Be(1);
        triggeredPage.Items.Single().Declaration.JobKey.Should().Be("jobs.alpha");

        var searchPage = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { SearchText = "zulu" }, TestContext.Current.CancellationToken);
        searchPage.TotalCount.Should().Be(1);

        var descriptionSearchPage = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { SearchText = "description-search-needle" }, TestContext.Current.CancellationToken);
        descriptionSearchPage.TotalCount.Should().Be(1);

        var sortedByJobKey = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { SortField = JobDefinitionSortField.JobKey }, TestContext.Current.CancellationToken);
        sortedByJobKey.Items.Select(definition => definition.Id)
            .Should().Equal(
                [
                    new JobId(StoreFixture.OWNER_A, "jobs.alpha"),
                    new JobId(StoreFixture.OWNER_B, "jobs.alpha"),
                    new JobId(StoreFixture.OWNER_A, "jobs.zulu")
                ]);

        var firstPage = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { PageSize = 2, PageNumber = 1, SortField = JobDefinitionSortField.JobKey }, TestContext.Current.CancellationToken);
        var secondPage = await fixture.Store.QueryDefinitionsAsync(
            fixture.Scope,
            new JobDefinitionQuery { PageSize = 2, PageNumber = 2, SortField = JobDefinitionSortField.JobKey }, TestContext.Current.CancellationToken);
        firstPage.Items.Select(definition => definition.Id)
            .Concat(secondPage.Items.Select(definition => definition.Id))
            .Should().Equal(sortedByJobKey.Items.Select(definition => definition.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionQueriesAndStatistics_ShouldStayBoundedAndFiltered(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(
            StoreFixture.OWNER_A,
            StoreFixture.TriggeredDeclaration("jobs.beta"),
            StoreFixture.TriggeredDeclaration("jobs.other"));
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: "q-1");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.other", instanceId: "q-2");

        var byOwner = await fixture.Store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            OwnerKey = StoreFixture.OWNER_B
        }, TestContext.Current.CancellationToken);
        byOwner.TotalCount.Should().Be(0);

        var byJob = await fixture.Store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            JobKey = "jobs.beta"
        }, TestContext.Current.CancellationToken);
        byJob.TotalCount.Should().Be(1);

        var statistics = await fixture.Store.GetExecutionStateStatisticsAsync(fixture.Scope, cancellationToken: TestContext.Current.CancellationToken);
        statistics[JobExecutionState.Queued].Should().Be(2);

        var latest = await fixture.Store.GetLatestExecutionsAsync(
            fixture.Scope,
            StoreFixture.OWNER_A,
            [new JobId(StoreFixture.OWNER_A, "jobs.beta"), new JobId(StoreFixture.OWNER_A, "jobs.missing")], TestContext.Current.CancellationToken);
        latest[new JobId(StoreFixture.OWNER_A, "jobs.beta")]!.InstanceId.Should().Be("q-1");
        latest[new JobId(StoreFixture.OWNER_A, "jobs.missing")].Should().BeNull();

        Func<Task> mismatchedOwner = () => fixture.Store.GetLatestExecutionsAsync(
            fixture.Scope,
            StoreFixture.OWNER_A,
            [new JobId(StoreFixture.OWNER_B, "jobs.beta")],
            TestContext.Current.CancellationToken);
        await mismatchedOwner.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryCleanup_ShouldApplyRetentionPoliciesAndBoundedDeletion(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.TriggeredDeclaration("jobs.beta"));
        for (var i = 0; i < 5; i++)
        {
            await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_A, "jobs.beta", instanceId: $"done-{i}");
            var lease = (await fixture.ClaimAsync(StoreFixture.OWNER_A, "worker-a", ["jobs.beta"])).Single();
            await fixture.Store.CompleteAttemptAsync(new JobAttemptCompletion
            {
                LeaseKey = lease.LeaseKey,
                Outcome = JobAttemptOutcome.Succeeded
            }, TestContext.Current.CancellationToken);
            fixture.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var candidates = await fixture.Store.GetExecutionCleanupCandidatesAsync(
            fixture.Scope,
            new Dictionary<JobId, JobHistoryRetentionPolicy>
            {
                [new JobId(StoreFixture.OWNER_A, "jobs.beta")] = new() { MaxRecords = 2 }
            },
            maxRetainedOrphanedExecutions: 0,
            maxDeletions: 10, TestContext.Current.CancellationToken);
        candidates.Should().HaveCount(3);
        var deleted = await fixture.Store.DeleteExecutionsAsync(fixture.Scope, candidates, TestContext.Current.CancellationToken);
        deleted.Should().Be(3);

        var remaining = await fixture.Store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = fixture.Scope,
            States = [JobExecutionState.Succeeded]
        }, TestContext.Current.CancellationToken);
        remaining.TotalCount.Should().Be(2);
    }

    private static async Task<RecurringMaterializationResult> MaterializeDueAsync(
        StoreFixture fixture,
        RecurringScheduleCursorKey key)
    {
        var due = (await fixture.Store.GetDueRecurringSchedulesAsync(fixture.Scope, key.OwnerKey, 10, TestContext.Current.CancellationToken))
            .SingleOrDefault(cursor => cursor.Key.JobKey == key.JobKey)
            ?? throw new InvalidOperationException($"Cursor '{key.JobKey}' is not due.");
        var occurrence = due.NextOccurrenceUtc!.Value;
        return await fixture.Store.TryMaterializeRecurringOccurrenceAsync(new RecurringOccurrenceMaterialization
        {
            CursorKey = due.Key,
            ExpectedVersion = due.Version,
            ExpectedOccurrenceUtc = occurrence,
            InstanceId = $"occurrence-{key.JobKey}-{occurrence.UtcTicks}"
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryOwnerSummaries_ShouldAggregatePresenceQueueDepthAndFreshness(bool useEfCore)
    {
        await using var fixture = await StoreFixture.CreateAsync(useEfCore, NOW);
        await fixture.SyncAsync(StoreFixture.OWNER_A,
            StoreFixture.RecurringDeclaration("jobs.alpha"),
            StoreFixture.RecurringDeclaration("jobs.beta"));
        await fixture.SyncAsync(StoreFixture.OWNER_B, StoreFixture.TriggeredDeclaration("jobs.gamma"));
        // Retire one owner-A definition and advance the clock so the surviving observation time is distinct.
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await fixture.SyncAsync(StoreFixture.OWNER_A, StoreFixture.RecurringDeclaration("jobs.beta"));
        var lastObserved = NOW.AddMinutes(1);
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_B, "jobs.gamma", instanceId: "instance-queued");
        await fixture.EnqueueTriggeredAsync(StoreFixture.OWNER_B, "jobs.gamma", instanceId: "instance-running");
        var claimed = (await fixture.ClaimAsync(
            StoreFixture.OWNER_B,
            "worker-1",
            ["jobs.gamma"],
            maxCount: 1)).Should().ContainSingle().Subject;
        claimed.Execution.State.Should().Be(JobExecutionState.Running);

        var summaries = await fixture.Store.QueryOwnerSummariesAsync(fixture.Scope, TestContext.Current.CancellationToken);

        summaries.Select(static summary => summary.OwnerKey).Should().Equal(StoreFixture.OWNER_A, StoreFixture.OWNER_B);
        var ownerA = summaries[0];
        ownerA.PresentCount.Should().Be(1);
        ownerA.AbsentCount.Should().Be(1);
        ownerA.QueuedCount.Should().Be(0);
        ownerA.RunningCount.Should().Be(0);
        ownerA.LastObservedAtUtc.Should().Be(lastObserved);
        var ownerB = summaries[1];
        ownerB.PresentCount.Should().Be(1);
        ownerB.AbsentCount.Should().Be(0);
        ownerB.QueuedCount.Should().Be(1);
        ownerB.RunningCount.Should().Be(1);
        ownerB.LastObservedAtUtc.Should().Be(NOW);
    }
}
