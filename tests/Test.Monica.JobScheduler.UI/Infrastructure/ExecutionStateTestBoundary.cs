using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.Modules;
using Monica.Testing.Localization;
using NSubstitute;
using Test.Monica.JobScheduler.UI.Support;

namespace Test.Monica.JobScheduler.UI.Infrastructure;

/// <summary>Exercises execution state with the real store and facade, controlling only async external boundaries.</summary>
internal sealed class ExecutionStateTestBoundary
{
    internal const string Scope = "execution-state-tests";
    internal const string Owner = " owner/运营?scope=%25 ";
    internal const string Job = " report/导出?#% ";
    internal const string ExactInstance = " execution/导出?revision=%25#保留 ";
    private readonly JobExecutionsStateFactory _factory;
    internal ManualClock Clock { get; } = new();
    internal InMemoryJobSchedulerStore Store { get; }
    internal ControlledAccess Access { get; } = new();
    internal IJobSchedulerStore Boundary { get; } = Substitute.For<IJobSchedulerStore>();
    internal List<JobExecutionQuery> Queries { get; } = [];
    internal List<string> DetailIds { get; } = [];
    internal List<string> CancellationIds { get; } = [];
    internal Func<JobExecutionQuery, CancellationToken, Task<QueryResult<JobExecutionInstance>>>? LoadExecutions { get; set; }
    internal Func<string, CancellationToken, Task<JobExecutionInstance?>>? LoadDetail { get; set; }
    internal Func<string, CancellationToken, Task<JobCancellationResult>>? CancelExecution { get; set; }

    internal ExecutionStateTestBoundary(TimeZoneInfo? timeZone = null)
    {
        Store = new(Clock);
        Boundary.QueryExecutionsAsync(Arg.Any<JobExecutionQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<JobExecutionQuery>(0);
                var token = call.ArgAt<CancellationToken>(1);
                Queries.Add(query);
                return LoadExecutions?.Invoke(query, token) ?? Store.QueryExecutionsAsync(query, token);
            });
        Boundary.GetExecutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(1);
                var token = call.ArgAt<CancellationToken>(2);
                DetailIds.Add(id);
                return LoadDetail?.Invoke(id, token) ?? Store.GetExecutionAsync(call.ArgAt<string>(0), id, token);
            });
        Boundary.RequestCancellationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(1);
                var token = call.ArgAt<CancellationToken>(3);
                CancellationIds.Add(id);
                return CancelExecution?.Invoke(id, token)
                    ?? Store.RequestCancellationAsync(call.ArgAt<string>(0), id, call.ArgAt<string?>(2), token);
            });
        var schedulerOptions = Options.Create(new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = Scope, CronTimeZone = timeZone ?? TimeZoneInfo.Utc
        });
        var facade = new JobSchedulerFacade(Boundary, schedulerOptions, Clock, new JobSchedulerRuntimeState(),
            NullLogger<JobSchedulerFacade>.Instance);
        var presentation = new SchedulerTimePresentation(schedulerOptions, new EchoStringLocalizer<JobSchedulerResource>(), SchedulerTimePresentationTests.CreateUiTimeFormatter());
        _factory = new(facade, Access, Options.Create(new ModuleJobSchedulerUIOption()), Clock, presentation);
    }

    internal JobExecutionsPageState CreatePage() => _factory.CreatePageState();
    internal ExecutionDetailState CreateDetail(string id = ExactInstance) => _factory.CreateDetailState(id);

    internal Task<JobDefinitionSyncResult> SeedAsync(CancellationToken token) => Store.SyncOwnerSnapshotAsync(
        new JobOwnerSnapshot(Scope, Owner,
        [new JobDeclaration
        {
            JobKey = Job, JobName = "Execution contract job", JobType = JobType.Triggered,
            JobArgsKey = "ExecutionContractArgs", MaxConcurrency = 10, RetryCount = 1
        }]), token);

    internal Task<JobExecutionInstance> EnqueueAsync(string id, CancellationToken token) => Store.EnqueueAsync(
        new JobEnqueueRequest
        {
            SchedulerScopeKey = Scope, OwnerKey = Owner, JobKey = Job, InstanceId = id, JobArgs = "{\"name\":\"review\"}"
        }, token);

    internal Task<IReadOnlyList<JobExecutionLease>> ClaimAsync(CancellationToken token) => Store.ClaimAsync(
        new JobClaimRequest
        {
            SchedulerScopeKey = Scope, OwnerKey = Owner, WorkerInstanceId = "review-worker", JobKeys = [Job],
            LeaseDuration = TimeSpan.FromMinutes(5), MaxCount = 10
        }, token);

    internal sealed class ControlledAccess : IJobSchedulerUiAccess
    {
        private int _calls;
        internal bool Authorized { get; set; } = true;
        internal Func<CancellationToken, Task<bool>>? Authorize { get; set; }
        internal int Calls => Volatile.Read(ref _calls);
        public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Authorize?.Invoke(cancellationToken) ?? Task.FromResult(Authorized);
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
