using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using Monica.Locker.Leases;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;
using Xunit.Sdk;

namespace Test.Monica.JobScheduler.Facades;

public sealed class JobSchedulerFacadeTests
{
    private static readonly DateTimeOffset NOW = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TriggerAsync_ShouldAdmitPresentDefinitionAndCaptureTemplate()
    {
        var harness = await FacadeHarness.CreateAsync();
        await harness.SyncDefinitions(
            StoreFixture.TriggeredDeclaration("jobs.beta"),
            StoreFixture.RecurringDeclaration("jobs.alpha"));

        var result = await harness.Facade.TriggerAsync(new JobTriggerRequest
        {
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.beta",
            JobArgs = "{\"v\":1}"
        }, TestContext.Current.CancellationToken);
        result.Status.Should().Be(ResStatus.Ok);
        result.Data!.State.Should().Be(JobExecutionState.Queued);
        result.Data.Template.JobKey.Should().Be("jobs.beta");
        result.Data.Template.OwnerKey.Should().Be(StoreFixture.OWNER_A);

        var missing = await harness.Facade.TriggerAsync(new JobTriggerRequest
        {
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.missing"
        }, TestContext.Current.CancellationToken);
        missing.Status.Should().NotBe(ResStatus.Ok);
    }

    [Fact]
    public async Task UpdatePolicyAsync_ShouldMapConflictNotFoundAndValidation()
    {
        var harness = await FacadeHarness.CreateAsync();
        await harness.SyncDefinitions(StoreFixture.RecurringDeclaration("jobs.alpha"));
        var definition = await harness.Facade.GetDefinitionAsync(new JobId(StoreFixture.OWNER_A, "jobs.alpha"), TestContext.Current.CancellationToken);
        definition.Data!.Policy.ConcurrencyStamp.Should().NotBeNullOrWhiteSpace();

        var conflict = await harness.Facade.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            new JobPolicyChange
            {
                Overrides = new JobPolicyOverrides(),
                ExpectedConcurrencyStamp = "00000000000000000000000000000000"
            }, TestContext.Current.CancellationToken);
        conflict.Status.Should().Be(ResStatus.Conflict);

        var notFound = await harness.Facade.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.missing",
            new JobPolicyChange
            {
                Overrides = new JobPolicyOverrides(),
                ExpectedConcurrencyStamp = definition.Data.Policy.ConcurrencyStamp
            }, TestContext.Current.CancellationToken);
        notFound.Status.Should().Be(ResStatus.NotFound);

        var invalid = await harness.Facade.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            new JobPolicyChange
            {
                Overrides = new JobPolicyOverrides { MaxConcurrencyOverride = 0 },
                ExpectedConcurrencyStamp = definition.Data.Policy.ConcurrencyStamp
            }, TestContext.Current.CancellationToken);
        invalid.Status.Should().Be(ResStatus.BadRequest);

        var ok = await harness.Facade.UpdatePolicyAsync(
            StoreFixture.OWNER_A,
            "jobs.alpha",
            new JobPolicyChange
            {
                Overrides = new JobPolicyOverrides { MaxConcurrencyOverride = 3 },
                ExpectedConcurrencyStamp = definition.Data.Policy.ConcurrencyStamp
            }, TestContext.Current.CancellationToken);
        ok.Status.Should().Be(ResStatus.Ok);

        var batch = await harness.Facade.UpdatePoliciesAsync(new JobPolicyBatchUpdateRequest
        {
            Items =
            [
                new JobPolicyBatchUpdateItem
                {
                    OwnerKey = StoreFixture.OWNER_A,
                    JobKey = "jobs.alpha",
                    Overrides = new JobPolicyOverrides(),
                    ExpectedConcurrencyStamp = "00000000000000000000000000000000"
                },
                new JobPolicyBatchUpdateItem
                {
                    OwnerKey = StoreFixture.OWNER_A,
                    JobKey = "jobs.missing",
                    Overrides = new JobPolicyOverrides(),
                    ExpectedConcurrencyStamp = "x"
                }
            ]
        }, TestContext.Current.CancellationToken);
        batch.Data!.SucceededCount.Should().Be(0);
        batch.Data.FailedCount.Should().Be(2);
        batch.Data.Items[0].FailureStatus.Should().Be(ResStatus.Conflict);
        batch.Data.Items[1].FailureStatus.Should().Be(ResStatus.NotFound);
    }

    [Fact]
    public async Task GetOverviewAsync_ShouldCombineDefinitionsQueueAndRecentActivity()
    {
        var harness = await FacadeHarness.CreateAsync();
        await harness.SyncDefinitions(
            StoreFixture.TriggeredDeclaration("jobs.beta"),
            StoreFixture.RecurringDeclaration("jobs.alpha"));
        await harness.Facade.RunRecurringNowAsync(new JobRecurringRunNowRequest
        {
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.alpha"
        }, TestContext.Current.CancellationToken);

        var overview = await harness.Facade.GetOverviewAsync(TestContext.Current.CancellationToken);
        overview.Status.Should().Be(ResStatus.Ok);
        overview.Data!.Definitions.Should().HaveCount(2);
        overview.Data.ExecutionStateCounts[JobExecutionState.Queued].Should().Be(1);
        overview.Data.RecentExecutions.Should().ContainSingle();
        // One execution admitted inside the trailing 24h window averages 1/24 executions per hour.
        overview.Data.ExecutionsPerHourLast24h.Should().BeApproximately(1 / 24d, 0.0001);
    }

    [Fact]
    public async Task QueryAndDetail_ShouldStayBoundedAndScoped()
    {
        var harness = await FacadeHarness.CreateAsync();
        await harness.SyncDefinitions(StoreFixture.TriggeredDeclaration("jobs.beta"));
        var triggered = await harness.Facade.TriggerAsync(new JobTriggerRequest
        {
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.beta",
            JobArgs = "{\"v\":1}"
        }, TestContext.Current.CancellationToken);

        var page = await harness.Facade.QueryExecutionsAsync(new JobExecutionQuery { SchedulerScopeKey = StoreFixture.SCOPE, PageSize = 5 }, TestContext.Current.CancellationToken);
        page.Data!.TotalCount.Should().Be(1);

        var detail = await harness.Facade.GetExecutionAsync(triggered.Data!.InstanceId, TestContext.Current.CancellationToken);
        detail.Data!.History.Should().NotBeEmpty();

        var summaries = await harness.Facade.QueryOperationalSummariesAsync(new JobDefinitionQuery(), TestContext.Current.CancellationToken);
        summaries.Data!.TotalCount.Should().Be(1);
        summaries.Data.Items.Single().Definition.Id.Should().Be(new JobId(StoreFixture.OWNER_A, "jobs.beta"));

        var summary = await harness.Facade.GetOperationalSummaryAsync(new JobId(StoreFixture.OWNER_A, "jobs.beta"), TestContext.Current.CancellationToken);
        summary.Data!.LatestExecution!.InstanceId.Should().Be(triggered.Data.InstanceId);
    }

    [Fact]
    public async Task GetRuntimeOverviewAsync_ShouldCaptureConfigurationStoreLocalPlaneAndOwners()
    {
        var harness = await FacadeHarness.CreateAsync();
        await harness.SyncDefinitions(StoreFixture.RecurringDeclaration("jobs.alpha"));
        var enqueued = await harness.Store.RunRecurringNowAsync(new JobRecurringRunNowCommand
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = StoreFixture.OWNER_A,
            JobKey = "jobs.alpha",
            InstanceId = "runtime-overview-run-now"
        }, TestContext.Current.CancellationToken);
        enqueued.State.Should().Be(JobExecutionState.Queued);
        harness.RuntimeState.SetScheduling(true, "Scheduling is active.");
        harness.RuntimeState.SetWorker(true, "Worker is active.");
        harness.RuntimeState.SetInFlightExecutions(2);
        harness.RuntimeState.RecordOwnerSchedulingWork(NOW);
        harness.RuntimeState.RecordLeaseRecoveryWork(NOW.AddSeconds(-1));
        await harness.Store.CoordinatorLeases.TryAcquireAsync(
            new LeaseResource(StoreFixture.SCOPE, "jobscheduler:maintenance"), "diagnostic-holder", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var result = await harness.Facade.GetRuntimeOverviewAsync(TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResStatus.Ok);
        var overview = result.Data!;
        overview.Store.Kind.Should().Be("InMemory");
        overview.Store.Provider.Should().BeNull();
        overview.Configuration.SchedulerScopeKey.Should().Be(StoreFixture.SCOPE);
        overview.Configuration.OwnerKey.Should().Be(StoreFixture.OWNER_A);
        overview.Configuration.OwnerKeys.Should().Equal(StoreFixture.OWNER_A);
        overview.Configuration.SnapshotSyncInterval.Should().Be(new ModuleJobSchedulerOption().SnapshotSyncInterval);
        overview.OwnerOnlineThreshold.Should().Be(
            new ModuleJobSchedulerOption().SnapshotSyncInterval * 3,
            "the owner liveness contract must travel with the snapshot");
        overview.LocalPlane.SchedulingReady.Should().BeTrue();
        overview.LocalPlane.SchedulingMessage.Should().Be("Scheduling is active.");
        overview.LocalPlane.WorkerReady.Should().BeTrue();
        overview.LocalPlane.InFlightExecutions.Should().Be(2);
        overview.LocalPlane.LastOwnerSchedulingWorkAtUtc.Should().Be(NOW);
        overview.LocalPlane.LastLeaseRecoveryWorkAtUtc.Should().Be(NOW.AddSeconds(-1));
        overview.LocalPlane.LastHistoryCleanupWorkAtUtc.Should().BeNull();
        overview.Configuration.MaxConcurrentStoreOperations.Should().Be(8);
        overview.Configuration.ReservedLeaseStoreOperations.Should().Be(2);
        overview.Configuration.StoreOperationTimeout.Should().Be(TimeSpan.FromSeconds(5));
        overview.Coordination!.Coordinators.Should().ContainSingle().Which.WorkerInstanceId.Should().Be("diagnostic-holder");
        var owner = overview.Owners.Should().ContainSingle().Subject;
        owner.OwnerKey.Should().Be(StoreFixture.OWNER_A);
        owner.PresentCount.Should().Be(1);
        owner.AbsentCount.Should().Be(0);
        owner.QueuedCount.Should().Be(1);
        owner.RunningCount.Should().Be(0);
        owner.LastObservedAtUtc.Should().Be(NOW);
    }

    private sealed class FacadeHarness
    {
        private FacadeHarness(
            JobSchedulerFacade facade,
            InMemoryJobSchedulerStore store,
            JobSchedulerRuntimeState runtimeState)
        {
            Facade = facade;
            Store = store;
            RuntimeState = runtimeState;
        }

        internal JobSchedulerFacade Facade { get; }

        internal InMemoryJobSchedulerStore Store { get; }

        internal JobSchedulerRuntimeState RuntimeState { get; }

        internal Task SyncDefinitions(params JobDeclaration[] declarations) =>
            Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
                StoreFixture.SCOPE,
                StoreFixture.OWNER_A,
                declarations));

        internal static Task<FacadeHarness> CreateAsync()
        {
            var time = new StoreFixture.ManualTimeProvider(NOW);
            var store = new InMemoryJobSchedulerStore(time);
            var options = Options.Create(new ModuleJobSchedulerOption
            {
                SchedulerScopeKey = StoreFixture.SCOPE,
                ProjectName = StoreFixture.OWNER_A
            });
            var runtimeState = new JobSchedulerRuntimeState();
            var facade = new JobSchedulerFacade(
                store,
                options,
                time,
                runtimeState,
                NullLogger<JobSchedulerFacade>.Instance);
            return Task.FromResult(new FacadeHarness(facade, store, runtimeState));
        }
    }
}
