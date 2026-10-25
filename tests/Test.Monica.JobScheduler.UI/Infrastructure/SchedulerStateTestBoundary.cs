using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.Modules;
using Monica.Testing.Localization;
using NSubstitute;
using Test.Monica.JobScheduler.UI.Support;

namespace Test.Monica.JobScheduler.UI.Infrastructure;

/// <summary>Uses the real volatile store and facade, with explicit controllable read boundaries and no renderer.</summary>
internal sealed class SchedulerStateTestBoundary
{
    internal const string Scope = "scheduler-state-tests";
    internal const string Owner = " owner/运营?revision=%25 ";
    internal const string Job = " report/导出?#% ";
    private readonly EchoStringLocalizer<JobSchedulerResource> _localizer = new();
    private readonly ModuleJobSchedulerOption _options = new() { SchedulerScopeKey = Scope, CronTimeZone = TimeZoneInfo.Utc };
    private readonly JobSchedulerFacade _facade;
    internal InMemoryJobSchedulerStore Store { get; } = new(TimeProvider.System);
    internal ControllableAccess Access { get; } = new();
    internal IJobSchedulerStore Boundary { get; } = Substitute.For<IJobSchedulerStore>();
    internal Exception? FacetFailure { get; set; }
    internal Func<JobDefinitionQuery, CancellationToken, Task<QueryResult<JobOperationalSummary>>>? LoadCatalog { get; set; }
    internal Func<JobId, CancellationToken, Task<JobOperationalSummary?>>? LoadDetail { get; set; }
    internal List<JobDefinitionQuery> CatalogQueries { get; } = [];
    internal List<JobId> DetailQueries { get; } = [];

    internal SchedulerStateTestBoundary()
    {
        Boundary.QueryDefinitionsAsync(Arg.Any<string>(), Arg.Any<JobDefinitionQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => FacetFailure is { } failure
                ? Task.FromException<QueryResult<JobDefinition>>(failure)
                : Store.QueryDefinitionsAsync(call.ArgAt<string>(0), call.ArgAt<JobDefinitionQuery>(1), call.ArgAt<CancellationToken>(2)));
        Boundary.GetExecutionStateStatisticsAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(call => Store.GetExecutionStateStatisticsAsync(call.ArgAt<string>(0), call.ArgAt<DateTimeOffset?>(1), call.ArgAt<DateTimeOffset?>(2), call.ArgAt<CancellationToken>(3)));
        Boundary.QueryExecutionsAsync(Arg.Any<JobExecutionQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => Store.QueryExecutionsAsync(call.ArgAt<JobExecutionQuery>(0), call.ArgAt<CancellationToken>(1)));
        Boundary.QueryOperationalSummariesAsync(Arg.Any<string>(), Arg.Any<JobDefinitionQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.ArgAt<JobDefinitionQuery>(1);
                var token = call.ArgAt<CancellationToken>(2);
                CatalogQueries.Add(query);
                return LoadCatalog?.Invoke(query, token) ?? Store.QueryOperationalSummariesAsync(call.ArgAt<string>(0), query, token);
            });
        Boundary.GetOperationalSummaryAsync(Arg.Any<string>(), Arg.Any<JobId>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var jobId = call.ArgAt<JobId>(1);
                var token = call.ArgAt<CancellationToken>(2);
                DetailQueries.Add(jobId);
                return LoadDetail?.Invoke(jobId, token) ?? Store.GetOperationalSummaryAsync(call.ArgAt<string>(0), jobId, token);
            });
        Boundary.GetExecutionAnalyticsAsync(Arg.Any<string>(), Arg.Any<JobExecutionAnalyticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => Store.GetExecutionAnalyticsAsync(call.ArgAt<string>(0), call.ArgAt<JobExecutionAnalyticsQuery>(1), call.ArgAt<CancellationToken>(2)));
        Boundary.UpdatePolicyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JobPolicyChange>(), Arg.Any<CancellationToken>())
            .Returns(call => Store.UpdatePolicyAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<JobPolicyChange>(3), call.ArgAt<CancellationToken>(4)));
        Boundary.RunRecurringNowAsync(Arg.Any<JobRecurringRunNowCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => Store.RunRecurringNowAsync(call.ArgAt<JobRecurringRunNowCommand>(0), call.ArgAt<CancellationToken>(1)));
        _facade = new JobSchedulerFacade(Boundary, Options.Create(_options), TimeProvider.System,
            new JobSchedulerRuntimeState(), NullLogger<JobSchedulerFacade>.Instance);
    }

    internal JobCatalogPageState CreateCatalog() => new(_facade, Access, _localizer, 20);
    internal JobDefinitionDetailPageState CreateDetail(JobId? id = null) => new(_facade, Access, TimeProvider.System,
        new SchedulerTimePresentation(Options.Create(_options), _localizer, SchedulerTimePresentationTests.CreateUiTimeFormatter()), _localizer, id ?? new JobId(Owner, Job));

    internal Task SeedAsync(CancellationToken cancellationToken) => Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(Scope, Owner,
    [
        new JobDeclaration
        {
            JobKey = Job, JobName = "Review job", JobType = JobType.Recurring,
            CronExpression = "0 */5 * * * *", TimeZoneId = "UTC"
        },
        new JobDeclaration
        {
            JobKey = "another-job", JobName = "Another review job", JobType = JobType.Recurring,
            CronExpression = "0 */5 * * * *", TimeZoneId = "UTC"
        }
    ]), cancellationToken);

    internal sealed class ControllableAccess : IJobSchedulerUiAccess
    {
        internal bool Authorized { get; set; } = true;
        internal Func<CancellationToken, Task<bool>>? Authorize { get; set; }
        internal int Calls { get; private set; }
        public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Authorize?.Invoke(cancellationToken) ?? Task.FromResult(Authorized);
        }
    }
}
