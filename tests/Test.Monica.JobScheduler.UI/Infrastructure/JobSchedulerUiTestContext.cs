using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.JobScheduler.UI.Localization;
using Monica.JobScheduler.UI.UIJobScheduler.Abstractions;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.Shared;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using Monica.JobScheduler.UI.UIJobScheduler.Support;
using Monica.Modules;
using Monica.Testing.Localization;
using Monica.Testing.UI;
using Monica.UI.Shell.State;
using Test.Monica.JobScheduler.UI.Support;
using MudBlazor;
using MudBlazor.Services;

namespace Test.Monica.JobScheduler.UI.Infrastructure;

public sealed class JobSchedulerUiTestContext : BunitContext
{
    public const string SCOPE = "job-scheduler-ui-tests";
    public const string OWNER = "worker-a";

    public JobSchedulerUiTestContext(
        bool isAuthorized = true,
        TimeZoneInfo? schedulerTimeZone = null,
        TimeProvider? timeProvider = null,
        IJobSchedulerWorkerInsightProvider? workerInsight = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var schedulerClock = timeProvider ?? TimeProvider.System;
        var configuredTimeZone = schedulerTimeZone ?? TimeZoneInfo.Utc;
        Store = new InMemoryJobSchedulerStore(schedulerClock);
        SeedStore(configuredTimeZone);

        var schedulerOptions = new ModuleJobSchedulerOption
        {
            SchedulerScopeKey = SCOPE,
            ProjectName = OWNER,
            CronTimeZone = configuredTimeZone
        };
        var facade = new JobSchedulerFacade(
            Store,
            Options.Create(schedulerOptions),
            schedulerClock,
            new JobSchedulerRuntimeState(),
            NullLogger<JobSchedulerFacade>.Instance);

        Services.AddMudServices();
        Services.AddSingleton<IStringLocalizer<global::Monica.UI.Localization.SharedResource>, EchoStringLocalizer<global::Monica.UI.Localization.SharedResource>>();
        Services.AddSingleton<IStringLocalizer<JobSchedulerResource>, EchoStringLocalizer<JobSchedulerResource>>();
        Services.AddSingleton<IThemeState, TestThemeState>();
        Services.AddSingleton<IOptions<ModuleJobSchedulerOption>>(Options.Create(schedulerOptions));
        Services.AddSingleton<TimeProvider>(schedulerClock);
        Services.AddSingleton<IOptions<ModuleJobSchedulerUIOption>>(Options.Create(new ModuleJobSchedulerUIOption
        {
            AutoRefreshInterval = TimeSpan.Zero,
            DefaultPageSize = 20
        }));
        Access = new TestJobSchedulerUiAccess(isAuthorized);
        Services.AddSingleton<IJobSchedulerUiAccess>(Access);
        Services.AddSingleton(facade);
        Services.AddSingleton(SchedulerTimePresentationTests.CreateUiTimeFormatter());
        Services.AddScoped<SchedulerTimePresentation>();
        Services.AddScoped<SchedulerOverviewPageStateFactory>();
        Services.AddScoped<JobCatalogPageStateFactory>();
        Services.AddScoped<JobDefinitionDetailPageStateFactory>();
        Services.AddScoped<JobExecutionsStateFactory>();
        Services.AddScoped<SchedulerStatisticsPageStateFactory>();
        Services.AddScoped<SchedulerRuntimePageStateFactory>();
        if (workerInsight is not null)
        {
            Services.AddSingleton(workerInsight);
        }
        PopoverProvider = Render<MudPopoverProvider>();
        DialogProvider = Render<MudDialogProvider>();
    }

    public InMemoryJobSchedulerStore Store { get; }

    public IRenderedComponent<MudDialogProvider> DialogProvider { get; }

    public IRenderedComponent<MudPopoverProvider> PopoverProvider { get; }

    internal TestJobSchedulerUiAccess Access { get; }

    public JobDefinition TriggeredDefinition { get; private set; } = null!;

    public JobDefinition RecurringDefinition { get; private set; } = null!;

    private void SeedStore(TimeZoneInfo configuredTimeZone)
    {
        Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
            SCOPE,
            OWNER,
            [
                new JobDeclaration
                {
                    JobKey = "Sample.Jobs.RecurringCleanup",
                    JobName = "Recurring cleanup",
                    JobType = JobType.Recurring,
                    CronExpression = "0 */5 * * * *",
                    TimeZoneId = configuredTimeZone.Id,
                    MaxConcurrency = 1
                },
                new JobDeclaration
                {
                    JobKey = "Sample.Jobs.GenerateReport",
                    JobArgsKey = "Sample.Jobs.GenerateReportArgs",
                    JobName = "Generate report",
                    JobType = JobType.Triggered,
                    MaxConcurrency = 2
                }
            ])).GetAwaiter().GetResult();
        TriggeredDefinition = Store.GetDefinitionAsync(SCOPE, OWNER, "Sample.Jobs.GenerateReport")
            .GetAwaiter().GetResult()!;
        RecurringDefinition = Store.GetDefinitionAsync(SCOPE, OWNER, "Sample.Jobs.RecurringCleanup")
            .GetAwaiter().GetResult()!;
        Store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
        {
            CursorKey = new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = SCOPE,
                OwnerKey = OWNER,
                JobKey = RecurringDefinition.Declaration.JobKey
            },
            HostSuspensionReasons = JobRecurringScheduleSuspensionReason.None
        }).GetAwaiter().GetResult();
    }

    internal sealed class TestJobSchedulerUiAccess(bool isAuthorized) : IJobSchedulerUiAccess
    {
        private TaskCompletionSource<bool>? _authorizationGate;
        private TaskCompletionSource<bool>? _authorizationStarted;

        internal bool IsAuthorized { get; set; } = isAuthorized;

        internal Exception? AuthorizationException { get; set; }

        internal Task AuthorizationStarted => _authorizationStarted?.Task ?? Task.CompletedTask;

        internal void BlockAuthorization()
        {
            _authorizationStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _authorizationGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _authorizationStarted?.TrySetResult(true);
            if (_authorizationGate is not null)
            {
                await _authorizationGate.Task.WaitAsync(cancellationToken);
            }

            if (AuthorizationException is not null)
            {
                throw AuthorizationException;
            }

            return IsAuthorized;
        }
    }
}
