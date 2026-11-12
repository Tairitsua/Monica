using Monica.Testing.Time;
using Monica.Locker.Leases;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Services;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using NSubstitute;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Services;

public sealed class JobSchedulingHostedServiceTests
{
    private static readonly DateTimeOffset NOW = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SelectedRoleWithoutJobs_RetiresOnlyItsOwnSnapshot()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var previous = await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_A,
            [StoreFixture.RecurringDeclaration("removed")]), TestContext.Current.CancellationToken);
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B,
            [StoreFixture.RecurringDeclaration("foreign")]), TestContext.Current.CancellationToken);
        var options = new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            EnableHistoryCleanup = false,
            OwnerManifestReplacements = new(StringComparer.Ordinal) { [StoreFixture.OWNER_A] = previous.ManifestHash }
        };
        using var service = CreateService(store, [], time, configuredOptions: options, activation: new EmptySelectedRole());
        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        Assert.False((await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_A, "removed", TestContext.Current.CancellationToken))!.IsPresent);
        Assert.True((await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_B, "foreign", TestContext.Current.CancellationToken))!.IsPresent);
    }

    private sealed class EmptySelectedRole : ITypeActivationPolicy
    {
        public IReadOnlyCollection<string> ActiveWorkloadOwners => [StoreFixture.OWNER_A];
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) => ActivationMode.Automatic;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => StoreFixture.OWNER_A;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCatalog_ShouldDistinguishStandaloneFallbackFromAnExplicitEmptyOwnerInventory(bool applicationInventory)
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var previous = await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_A,
            [StoreFixture.TriggeredDeclaration("previous")]), TestContext.Current.CancellationToken);
        var options = new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            EnableHistoryCleanup = false,
            OwnerManifestReplacements = new(StringComparer.Ordinal) { [StoreFixture.OWNER_A] = previous.ManifestHash }
        };
        using var service = CreateService(store, [], time, configuredOptions: options,
            activation: new EmptyInventory(applicationInventory));
        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(applicationInventory, (await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_A,
            "previous", TestContext.Current.CancellationToken))!.IsPresent);
    }

    private sealed class EmptyInventory(bool applicationInventory) : ITypeActivationPolicy
    {
        public IReadOnlyCollection<string>? ActiveWorkloadOwners => applicationInventory ? [] : null;
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) => ActivationMode.Automatic;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }

    [Fact]
    public async Task ScheduleAsync_WhenSeveralOwnersShareAHost_ShouldShareTheCycleBudgetAndPreserveReplicaIdentities()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        LocalJobDefinition[] definitions =
        [
            new()
            {
                OwnerKey = StoreFixture.OWNER_A,
                Declaration = StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"),
                JobClrType = typeof(object)
            },
            new()
            {
                OwnerKey = StoreFixture.OWNER_B,
                Declaration = StoreFixture.RecurringDeclaration("jobs.beta", cron: "0 0 * * * *"),
                JobClrType = typeof(object)
            }
        ];
        var options = new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = "combined-host",
            WorkerInstanceId = "combined-first-replica",
            MaxRecurringMaterializationsPerCycle = 1,
            EnableHistoryCleanup = false
        };
        using var firstReplica = CreateService(store, definitions, time, configuredOptions: options);
        using var secondReplica = CreateService(store, definitions, time, configuredOptions: new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = "combined-host",
            WorkerInstanceId = "combined-second-replica",
            MaxRecurringMaterializationsPerCycle = 1,
            EnableHistoryCleanup = false
        });
        await firstReplica.ScheduleAsync(TestContext.Current.CancellationToken);
        await secondReplica.ScheduleAsync(TestContext.Current.CancellationToken);
        (await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_A, "jobs.alpha",
            TestContext.Current.CancellationToken))!.IsPresent.Should().BeTrue();
        (await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_B, "jobs.beta",
            TestContext.Current.CancellationToken))!.IsPresent.Should().BeTrue();
        (await store.GetDefinitionAsync(StoreFixture.SCOPE, "combined-host", "jobs.alpha",
            TestContext.Current.CancellationToken)).Should().BeNull();

        time.Advance(TimeSpan.FromHours(1));
        await firstReplica.ScheduleAsync(TestContext.Current.CancellationToken);
        var firstCycle = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE
        }, TestContext.Current.CancellationToken);
        firstCycle.Items.Should().ContainSingle().Which.Template.OwnerKey
            .Should().BeOneOf(StoreFixture.OWNER_A, StoreFixture.OWNER_B);

        await firstReplica.ScheduleAsync(TestContext.Current.CancellationToken);
        await secondReplica.ScheduleAsync(TestContext.Current.CancellationToken);
        var afterReplicas = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE
        }, TestContext.Current.CancellationToken);
        afterReplicas.TotalCount.Should().Be(2);
        afterReplicas.Items.Select(execution => execution.Template.OwnerKey)
            .Should().BeEquivalentTo(new[] { StoreFixture.OWNER_A, StoreFixture.OWNER_B });
    }

    [Fact]
    public void CreateRecurringInstanceId_ShouldPreserveFieldBoundaries()
    {
        var first = JobSchedulingHostedService.CreateRecurringInstanceId(
            new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = "scope|owner",
                OwnerKey = "job",
                JobKey = "key"
            },
            NOW);
        var second = JobSchedulingHostedService.CreateRecurringInstanceId(
            new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = "scope",
                OwnerKey = "owner|job",
                JobKey = "key"
            },
            NOW);

        first.Should().NotBe(second);
    }

    [Fact]
    public async Task ScheduleAsync_ShouldSyncSnapshotMaterializeDueOccurrencesAndCoalesceOutages()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var definitions = new[]
        {
            new LocalJobDefinition
            {
                Declaration = StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"),
                JobClrType = typeof(object)
            }
        };
        using var service = CreateService(store, definitions, time);

        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        var operational = await store.GetOperationalSummaryAsync(
            StoreFixture.SCOPE,
            new JobId(StoreFixture.OWNER_A, "jobs.alpha"), TestContext.Current.CancellationToken);
        operational!.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Scheduled);
        operational.NextOccurrenceUtc.Should().Be(NOW.AddHours(1));

        // A long outage coalesces missed occurrences into one execution and advances to the first future occurrence.
        time.Advance(TimeSpan.FromHours(5));
        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        var page = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            JobKey = "jobs.alpha"
        }, TestContext.Current.CancellationToken);
        page.TotalCount.Should().Be(1);
        var materialized = page.Items.Single();
        materialized.Origin.Should().Be(JobExecutionOrigin.RecurringSchedule);
        materialized.RecurringOccurrenceUtc.Should().Be(NOW.AddHours(1));

        var afterOutage = await store.GetOperationalSummaryAsync(
            StoreFixture.SCOPE,
            new JobId(StoreFixture.OWNER_A, "jobs.alpha"), TestContext.Current.CancellationToken);
        afterOutage!.NextOccurrenceUtc.Should().Be(NOW.AddHours(6));
    }

    [Fact]
    public async Task ScheduleAsync_WhenTheManifestIsUnchangedAtTheSnapshotInterval_ShouldPreserveCursorState()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var definition = new LocalJobDefinition
        {
            Declaration = StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"),
            JobClrType = typeof(object)
        };
        var options = new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            SnapshotSyncInterval = TimeSpan.FromSeconds(5),
            CoordinatorLeaseDuration = TimeSpan.FromMinutes(1),
            EnableHistoryCleanup = false
        };
        using var service = CreateService(store, [definition], time, configuredOptions: options);

        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        var suspension = new RecurringScheduleSynchronization
        {
            CursorKey = new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = StoreFixture.SCOPE, OwnerKey = StoreFixture.OWNER_A,
                JobKey = definition.Declaration.JobKey
            },
            HostSuspensionReasons = JobRecurringScheduleSuspensionReason.Manual
        };
        var suspended = await store.SynchronizeRecurringScheduleAsync(suspension, TestContext.Current.CancellationToken);
        await service.ScheduleAsync(TestContext.Current.CancellationToken);

        var beforeInterval = await store.GetOperationalSummaryAsync(
            StoreFixture.SCOPE,
            new JobId(StoreFixture.OWNER_A, definition.Declaration.JobKey),
            TestContext.Current.CancellationToken);
        beforeInterval!.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Suspended);
        beforeInterval.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.Manual);

        time.Advance(options.SnapshotSyncInterval);
        await service.ScheduleAsync(TestContext.Current.CancellationToken);

        var afterInterval = await store.GetOperationalSummaryAsync(
            StoreFixture.SCOPE,
            new JobId(StoreFixture.OWNER_A, definition.Declaration.JobKey),
            TestContext.Current.CancellationToken);
        afterInterval!.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Suspended);
        afterInterval.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.Manual);
        var unchanged = await store.SynchronizeRecurringScheduleAsync(suspension, TestContext.Current.CancellationToken);
        unchanged.Status.Should().Be(RecurringScheduleSynchronizationStatus.Unchanged);
        unchanged.Cursor!.Version.Should().Be(suspended.Cursor!.Version);
        var owners = await store.QueryOwnerSummariesAsync(StoreFixture.SCOPE, TestContext.Current.CancellationToken);
        owners.Should().ContainSingle().Which.LastObservedAtUtc.Should().Be(NOW.Add(options.SnapshotSyncInterval));
    }

    [Fact]
    public async Task ScheduleAsync_InManualMode_ShouldSuppressAutomaticMaterialization()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var definitions = new[]
        {
            new LocalJobDefinition
            {
                Declaration = StoreFixture.RecurringDeclaration("jobs.alpha", cron: "0 0 * * * *"),
                JobClrType = typeof(object)
            }
        };
        using var service = CreateService(store, definitions, time, manualMode: true);
        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(2));
        await service.ScheduleAsync(TestContext.Current.CancellationToken);

        var operational = await store.GetOperationalSummaryAsync(
            StoreFixture.SCOPE,
            new JobId(StoreFixture.OWNER_A, "jobs.alpha"), TestContext.Current.CancellationToken);
        operational!.IsSuspended.Should().BeTrue();
        operational.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Suspended);
        var page = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            JobKey = "jobs.alpha"
        }, TestContext.Current.CancellationToken);
        page.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task RecoverAsync_WhenAnotherOwnersLeaseExpires_ShouldRecoverAcrossOwners()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            StoreFixture.SCOPE,
            StoreFixture.OWNER_B,
            [StoreFixture.TriggeredDeclaration("jobs.beta")]), TestContext.Current.CancellationToken);
        await store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B,
            JobKey = "jobs.beta",
            InstanceId = "other-owner-exec",
            JobArgs = "{\"v\":1}"
        }, TestContext.Current.CancellationToken);
        var lease = (await store.ClaimAsync(new JobClaimRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B,
            WorkerInstanceId = "worker-b",
            JobKeys = ["jobs.beta"],
            LeaseDuration = TimeSpan.FromSeconds(30)
        }, TestContext.Current.CancellationToken)).Single();

        time.Advance(TimeSpan.FromSeconds(31));
        using var service = CreateService(store, [], time);
        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        (await store.GetExecutionAsync(StoreFixture.SCOPE, "other-owner-exec", TestContext.Current.CancellationToken))!
            .State.Should().Be(JobExecutionState.Running);
        await service.RecoverAsync(TestContext.Current.CancellationToken);

        var recovered = await store.GetExecutionAsync(StoreFixture.SCOPE, "other-owner-exec", TestContext.Current.CancellationToken);
        recovered!.State.Should().Be(JobExecutionState.Queued);
        (await store.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status
            .Should().Be(JobLeaseRenewalStatus.Lost);
    }

    [Fact]
    public async Task RecoverAsync_WhenAnExpiredCancelledLeaseIsReleased_ShouldAllowTheNextSchedulingCycleToAdmitWork()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var definition = new LocalJobDefinition
        {
            Declaration = StoreFixture.RecurringDeclaration(
                "jobs.alpha",
                cron: "0 0 * * * *",
                maxConcurrency: 1),
            JobClrType = typeof(object)
        };
        using var service = CreateService(store, [definition], time);

        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));
        await service.ScheduleAsync(TestContext.Current.CancellationToken);

        var lease = (await store.ClaimAsync(new JobClaimRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_A,
            WorkerInstanceId = "worker-a",
            JobKeys = [definition.Declaration.JobKey],
            LeaseDuration = TimeSpan.FromMinutes(30)
        }, TestContext.Current.CancellationToken)).Single();
        (await store.RequestCancellationAsync(
            StoreFixture.SCOPE,
            lease.Execution.InstanceId,
            cancellationToken: TestContext.Current.CancellationToken)).Status
            .Should().Be(JobCancellationStatus.CancellationRequested);

        // Recovery and materialization are independent cycles. Once recovery releases the cancelled attempt,
        // the following scheduling cycle can use its capacity for the due occurrence.
        time.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)));
        await service.RecoverAsync(TestContext.Current.CancellationToken);
        await service.ScheduleAsync(TestContext.Current.CancellationToken);

        var executions = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            JobKey = definition.Declaration.JobKey
        }, TestContext.Current.CancellationToken);
        executions.Items.Should().HaveCount(2);
        executions.Items.Should().ContainSingle(item => item.State == JobExecutionState.Cancelled);
        executions.Items.Should().ContainSingle(item =>
            item.State == JobExecutionState.Queued
            && item.RecurringOccurrenceUtc == NOW.AddHours(2));
    }

    [Fact]
    public async Task ScheduleAsync_WhenTwoReplicasShareAnOwner_ShouldScheduleExclusivelyWhileTheStandbyMaintainsRemovedOwnerWork()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var removedOwner = await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B,
            [StoreFixture.TriggeredDeclaration("removed-owner-job")]), token);
        await store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B,
            JobKey = "removed-owner-job",
            InstanceId = "removed-owner-execution",
            JobArgs = "{}"
        }, token);
        var executionLease = (await store.ClaimAsync(new JobClaimRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B,
            WorkerInstanceId = "removed-owner-worker",
            JobKeys = ["removed-owner-job"],
            LeaseDuration = TimeSpan.FromSeconds(10)
        }, token)).Should().ContainSingle().Subject;
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B, [])
        {
            ExpectedPreviousManifestHash = removedOwner.ManifestHash
        }, token);
        LocalJobDefinition[] definitions =
        [
            new()
            {
                OwnerKey = StoreFixture.OWNER_A,
                Declaration = StoreFixture.RecurringDeclaration("local-job", cron: "*/10 * * * * *"),
                JobClrType = typeof(object)
            }
        ];
        using var first = CreateService(store, definitions, time, configuredOptions: new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            WorkerInstanceId = "first-replica",
            EnableHistoryCleanup = false
        });
        using var second = CreateService(store, definitions, time, configuredOptions: new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            WorkerInstanceId = "second-replica",
            EnableHistoryCleanup = false
        });
        await first.ScheduleAsync(token);
        await second.ScheduleAsync(token);
        time.Advance(TimeSpan.FromSeconds(11));

        await second.ScheduleAsync(token);
        var beforeHolder = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_A
        }, token);
        beforeHolder.TotalCount.Should().Be(0);
        await first.ScheduleAsync(token);
        await second.ScheduleAsync(token);
        var afterHolder = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_A
        }, token);
        afterHolder.Items.Should().ContainSingle().Which.RecurringOccurrenceUtc.Should().Be(NOW.AddSeconds(10));
        (await store.GetExecutionAsync(StoreFixture.SCOPE, executionLease.Execution.InstanceId, token))!
            .State.Should().Be(JobExecutionState.Running);

        // The standby for owner scheduling can independently hold scope maintenance, including retired owners.
        await second.RecoverAsync(token);
        await first.RecoverAsync(token);
        var recovered = await store.GetExecutionAsync(StoreFixture.SCOPE, executionLease.Execution.InstanceId, token);
        recovered!.State.Should().Be(JobExecutionState.Queued);
        recovered.LeaseLossCount.Should().Be(1);
        (await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_B, "removed-owner-job", token))!
            .IsPresent.Should().BeFalse();
        (await store.RenewLeaseAsync(executionLease.LeaseKey, TimeSpan.FromSeconds(30), token))
            .Status.Should().Be(JobLeaseRenewalStatus.Lost);
    }

    [Fact]
    public async Task CleanupHistoryAsync_WhenEnabled_ShouldCleanRemovedOwnerHistoryIndependentlyOfScheduling()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var published = await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B,
            [StoreFixture.TriggeredDeclaration("history-job")]), token);
        var definition = await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_B, "history-job", token);
        await store.UpdatePolicyAsync(StoreFixture.SCOPE, StoreFixture.OWNER_B, "history-job", new JobPolicyChange
        {
            ExpectedConcurrencyStamp = definition!.Policy.ConcurrencyStamp,
            Overrides = new JobPolicyOverrides { MaxRetainedHistoryRecords = 1 }
        }, token);
        for (var index = 0; index < 2; index++)
        {
            await store.EnqueueAsync(new JobEnqueueRequest
            {
                SchedulerScopeKey = StoreFixture.SCOPE,
                OwnerKey = StoreFixture.OWNER_B,
                JobKey = "history-job",
                InstanceId = $"history-{index}",
                JobArgs = "{}"
            }, token);
            var lease = (await store.ClaimAsync(new JobClaimRequest
            {
                SchedulerScopeKey = StoreFixture.SCOPE,
                OwnerKey = StoreFixture.OWNER_B,
                WorkerInstanceId = "history-worker",
                JobKeys = ["history-job"],
                LeaseDuration = TimeSpan.FromSeconds(30)
            }, token)).Should().ContainSingle().Subject;
            (await store.CompleteAttemptAsync(new JobAttemptCompletion
            {
                LeaseKey = lease.LeaseKey,
                Outcome = JobAttemptOutcome.Succeeded
            }, token)).Status.Should().Be(JobAttemptCompletionStatus.Applied);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B, [])
        {
            ExpectedPreviousManifestHash = published.ManifestHash
        }, token);
        using var service = CreateService(store, [], time, configuredOptions: new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            EnableHistoryCleanup = true
        });
        await service.ScheduleAsync(token);
        var beforeCleanup = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B
        }, token);
        beforeCleanup.TotalCount.Should().Be(2);

        await service.CleanupHistoryAsync(token);

        var afterCleanup = await store.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_B
        }, token);
        afterCleanup.Items.Should().ContainSingle().Which.InstanceId.Should().Be("history-1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduleAsync_WhenOneOwnerCycleCrossesGrantWindows_ShouldContinueOnlyWithTheOriginalGeneration(bool expiresBeforeRenewal)
    {
        var token = TestContext.Current.CancellationToken;
        var time = new ControllableTimeProvider(NOW);
        var durable = new InMemoryJobSchedulerStore(time);
        var boundary = Substitute.For<IJobSchedulerStore>();
        var mutationTokens = new List<string>();
        var synchronized = new List<string>();
        boundary.CoordinatorLeases.Returns(durable.CoordinatorLeases);
        var elapsed = TimeSpan.Zero;
        async Task AdvanceOperationAsync()
        {
            if (expiresBeforeRenewal && synchronized.Count == 1)
            {
                time.AdvanceAfterPause(TimeSpan.FromSeconds(31));
                return;
            }
            for (var step = 0; step < 2; step++)
            {
                await time.WaitForTimerAsync(elapsed + TimeSpan.FromSeconds(10), token);
                time.Advance(TimeSpan.FromSeconds(10));
                elapsed += TimeSpan.FromSeconds(10);
                await time.WaitForTimerAsync(elapsed + TimeSpan.FromSeconds(10), token);
            }
        }
        boundary.SyncOwnerSnapshotAsync(Arg.Any<JobOwnerSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(call => durable.SyncOwnerSnapshotAsync(call.Arg<JobOwnerSnapshot>(), call.Arg<CancellationToken>()));
        boundary.SynchronizeRecurringScheduleAsync(Arg.Any<RecurringScheduleSynchronization>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var command = call.Arg<RecurringScheduleSynchronization>();
                await AdvanceOperationAsync();
                var result = await durable.SynchronizeRecurringScheduleAsync(command, call.Arg<CancellationToken>());
                mutationTokens.Add(command.CoordinatorLease!.LeaseToken);
                synchronized.Add(command.CursorKey.JobKey);
                return result;
            });
        boundary.GetDueRecurringSchedulesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => durable.GetDueRecurringSchedulesAsync(call.ArgAt<string>(0), call.ArgAt<string>(1),
                call.Arg<int>(), call.Arg<CancellationToken>()));
        boundary.TryMaterializeRecurringOccurrenceAsync(Arg.Any<RecurringOccurrenceMaterialization>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var command = call.Arg<RecurringOccurrenceMaterialization>();
                await AdvanceOperationAsync();
                var result = await durable.TryMaterializeRecurringOccurrenceAsync(command, call.Arg<CancellationToken>());
                mutationTokens.Add(command.CoordinatorLease!.LeaseToken);
                return result;
            });
        var definitions = Enumerable.Range(0, 3).Select(index => new LocalJobDefinition
        {
            OwnerKey = StoreFixture.OWNER_A,
            Declaration = StoreFixture.RecurringDeclaration($"long-cycle-{index}", cron: "*/10 * * * * *"),
            JobClrType = typeof(object)
        }).ToArray();
        using var service = CreateService(boundary, definitions, time, configuredOptions: new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE, ProjectName = StoreFixture.OWNER_A,
            WorkerInstanceId = "long-cycle-worker", CoordinatorLeaseDuration = TimeSpan.FromSeconds(30),
            MaxRecurringMaterializationsPerCycle = 2, EnableHistoryCleanup = false
        });

        if (expiresBeforeRenewal)
        {
            Func<Task> cycle = () => service.ScheduleAsync(token);
            await cycle.Should().ThrowAsync<Exception>().Where(error => (error is InvalidOperationException) || (error is OperationCanceledException));
            synchronized.Should().ContainSingle();
            mutationTokens.Should().ContainSingle();
            (await durable.QueryExecutionsAsync(new JobExecutionQuery
            {
                SchedulerScopeKey = StoreFixture.SCOPE, OwnerKey = StoreFixture.OWNER_A
            }, token)).Items.Should().BeEmpty();
            return;
        }

        await service.ScheduleAsync(token);

        synchronized.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        mutationTokens.Should().HaveCount(5);
        mutationTokens.Distinct(StringComparer.Ordinal).Should().ContainSingle();
        time.GetUtcNow().Should().Be(NOW.AddSeconds(100));
        (await durable.QueryExecutionsAsync(new JobExecutionQuery
        {
            SchedulerScopeKey = StoreFixture.SCOPE, OwnerKey = StoreFixture.OWNER_A
        }, token)).Items.Should().HaveCount(2);
        (await durable.GetCoordinatorSnapshotAsync(StoreFixture.SCOPE, token)).Coordinators
            .Should().ContainSingle().Which.ExpiresAtUtc.Should().BeAfter(time.GetUtcNow());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalHistoryCoordination_ShouldNeverReplaceExecutionRecoveryFencing(bool unavailable)
    {
        var token = TestContext.Current.CancellationToken;
        var time = new ControllableTimeProvider(NOW);
        var durable = new InMemoryJobSchedulerStore(time);
        var boundary = Substitute.For<IJobSchedulerStore>();
        boundary.CoordinatorLeases.Returns(durable.CoordinatorLeases);
        boundary.RecoverExpiredLeasesAsync(Arg.Any<ExpiredLeaseRecoveryRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => durable.RecoverExpiredLeasesAsync(call.Arg<ExpiredLeaseRecoveryRequest>(), call.Arg<CancellationToken>()));
        boundary.CleanupHistoryAsync(Arg.Any<JobHistoryCleanupRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => durable.CleanupHistoryAsync(call.Arg<JobHistoryCleanupRequest>(), call.Arg<CancellationToken>()));
        IExclusiveLeaseStore external = new InMemoryLeaseStore(time);
        if (unavailable)
        {
            external = Substitute.For<IExclusiveLeaseStore>();
            external.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<LeaseGrant?>(new IOException("External authority unavailable")));
        }
        using var service = CreateService(boundary, [], time, configuredOptions: new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE, ProjectName = StoreFixture.OWNER_A, EnableHistoryCleanup = true,
            HistoryCleanupLeaseStore = new LeaseStoreSelection("advisory", "database-a")
        }, historyCleanupLeases: new(external));
        if (unavailable) await Assert.ThrowsAsync<IOException>(() => service.CleanupHistoryAsync(token));
        else
        {
            await service.CleanupHistoryAsync(token);
            await boundary.Received(1).CleanupHistoryAsync(Arg.Is<JobHistoryCleanupRequest>(request => request.CoordinatorLease == null),
                Arg.Any<CancellationToken>());
            Assert.Single((await external.GetSnapshotAsync("database-a", "jobscheduler:history:", token)).Leases);
        }
        await service.RecoverAsync(token);
        await boundary.Received(1).RecoverExpiredLeasesAsync(Arg.Is<ExpiredLeaseRecoveryRequest>(request =>
            request.CoordinatorLease != null && request.CoordinatorLease.CoordinatorKey == "maintenance"), Arg.Any<CancellationToken>());
        Assert.Single((await durable.CoordinatorLeases.GetSnapshotAsync(StoreFixture.SCOPE, "jobscheduler:maintenance", token)).Leases);
    }

    private static JobSchedulingHostedService CreateService(
        IJobSchedulerStore store,
        IReadOnlyList<LocalJobDefinition> definitions,
        TimeProvider time,
        bool manualMode = false,
        ModuleJobSchedulerOption? configuredOptions = null,
        ITypeActivationPolicy? activation = null,
        HistoryCleanupLeaseStore? historyCleanupLeases = null)
    {
        var options = configuredOptions ?? new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            ProjectName = StoreFixture.OWNER_A,
            AutonomousWork = !manualMode,
            EnableHistoryCleanup = false
        };
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        return new JobSchedulingHostedService(
            store,
            definitions,
            new JobSchedulerRuntimeState(),
            time,
            Options.Create(options),
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            Options.Create(new ModuleHostedServiceOption()),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<JobSchedulingHostedService>.Instance,
            activation, historyCleanupLeases);
    }
}
