using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Services;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
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
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_A,
            [StoreFixture.RecurringDeclaration("removed")]), TestContext.Current.CancellationToken);
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_B,
            [StoreFixture.RecurringDeclaration("foreign")]), TestContext.Current.CancellationToken);
        using var service = CreateService(store, [], time, activation: new EmptySelectedRole());
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
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, StoreFixture.OWNER_A,
            [StoreFixture.TriggeredDeclaration("previous")]), TestContext.Current.CancellationToken);
        using var service = CreateService(store, [], time, activation: new EmptyInventory(applicationInventory));
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
            MaxRecurringMaterializationsPerCycle = 1,
            EnableHistoryCleanup = false
        };
        using var firstReplica = CreateService(store, definitions, time, configuredOptions: options);
        using var secondReplica = CreateService(store, definitions, time, configuredOptions: options);
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
        firstCycle.Items.Should().ContainSingle().Which.Template.OwnerKey.Should().Be(StoreFixture.OWNER_A);

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
    public async Task ScheduleAsync_ShouldNotResynchronizeRecurringCursorsBeforeSnapshotInterval()
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
            EnableHistoryCleanup = false
        };
        using var service = CreateService(store, [definition], time, configuredOptions: options);

        await service.ScheduleAsync(TestContext.Current.CancellationToken);
        await store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
        {
            CursorKey = new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = StoreFixture.SCOPE, OwnerKey = StoreFixture.OWNER_A,
                JobKey = definition.Declaration.JobKey
            },
            HostSuspensionReasons = JobRecurringScheduleSuspensionReason.Manual
        }, TestContext.Current.CancellationToken);
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
        afterInterval!.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Scheduled);
        afterInterval.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.None);
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
    public async Task ScheduleAsync_WithExpiredLease_ShouldRecoverAcrossOwners()
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
        // The scheduling host of owner A recovers the expired lease of owner B.
        using var service = CreateService(store, [], time);
        await service.ScheduleAsync(TestContext.Current.CancellationToken);

        var recovered = await store.GetExecutionAsync(StoreFixture.SCOPE, "other-owner-exec", TestContext.Current.CancellationToken);
        recovered!.State.Should().Be(JobExecutionState.Queued);
        (await store.RenewLeaseAsync(lease.LeaseKey, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Status
            .Should().Be(JobLeaseRenewalStatus.Lost);
    }

    [Fact]
    public async Task ScheduleAsync_ShouldRecoverExpiredCancelledLeasesBeforeMaterializingDueOccurrences()
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

        // The lease is expired and the next hourly occurrence is due at the same time. Recovery must release the
        // cancelled attempt before recurring admission evaluates the capacity gate.
        time.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(1)));
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

    private static JobSchedulingHostedService CreateService(
        InMemoryJobSchedulerStore store,
        IReadOnlyList<LocalJobDefinition> definitions,
        StoreFixture.ManualTimeProvider time,
        bool manualMode = false,
        ModuleJobSchedulerOption? configuredOptions = null,
        ITypeActivationPolicy? activation = null)
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
            activation);
    }
}
