using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Results;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.UI.Components;
using Monica.JobScheduler.UI.Pages;
using Monica.JobScheduler.UI.UIJobScheduler.Executions.State;
using Monica.JobScheduler.UI.UIJobScheduler.Components;
using Monica.JobScheduler.UI.UIJobScheduler.State;
using MudBlazor;
using Test.Monica.JobScheduler.UI.Infrastructure;
using Xunit;

namespace Test.Monica.JobScheduler.UI.Pages;

public sealed class SchedulerOperationalPagesTests
{
    [Fact]
    public async Task DirectRoute_WhenCircuitIsUnauthorized_ShouldDenyBeforeLoadingSchedulerData()
    {
        await using var context = new JobSchedulerUiTestContext(isAuthorized: false);

        var cut = context.Render<JobCatalogPage>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Access:DeniedTitle"));
        cut.Markup.Should().NotContain(JobSchedulerUiTestContext.OWNER);
    }

    [Fact]
    public async Task CatalogState_ShouldPauseAndResumeRecurringMaterializationWithoutChangingDeclaration()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services.GetRequiredService<JobCatalogPageStateFactory>().Create(20);
        await state.InitializeAsync();
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);
        var recurring = state.Summaries.Single(summary =>
            summary.Definition.Declaration.JobType == JobType.Recurring);

        (await state.SetDisabledAsync(recurring, true)).Status.Should().Be(ResStatus.Ok);
        state.Summaries.Single(summary => summary.Definition.Declaration.JobKey ==
                                        recurring.Definition.Declaration.JobKey)
            .RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Suspended);

        var persisted = await context.Store.GetDefinitionAsync(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            recurring.Definition.Declaration.JobKey,
            Xunit.TestContext.Current.CancellationToken);
        persisted!.IsDisabled.Should().BeTrue();
        persisted.Declaration.CronExpression.Should().Be("0 */5 * * * *");
    }

    [Fact]
    public async Task CatalogState_ShouldAllowRunNowWhileRecurringMaterializationIsPaused()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services.GetRequiredService<JobCatalogPageStateFactory>().Create(20);
        await state.InitializeAsync();
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);
        var recurring = state.Summaries.Single();
        (await state.SetDisabledAsync(recurring, true)).Status.Should().Be(ResStatus.Ok);
        recurring = state.Summaries.Single();

        var result = await state.RunRecurringNowAsync(recurring);

        result.Status.Should().Be(ResStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.Origin.Should().Be(JobExecutionOrigin.RecurringRunNow);
        result.Data.State.Should().Be(JobExecutionState.Queued);
    }

    [Fact]
    public async Task CatalogState_WhenManualModeAloneSuppressesMaterialization_ShouldRejectQuickPauseAndAllowRunNow()
    {
        await using var context = new JobSchedulerUiTestContext();
        await SetManualSuppressionAsync(context);
        await using var state = context.Services.GetRequiredService<JobCatalogPageStateFactory>().Create(20);
        await state.InitializeAsync();
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);
        var recurring = state.Summaries.Single();
        recurring.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.Manual);

        var pause = await state.SetDisabledAsync(recurring, true);
        pause.IsFailed(out _).Should().BeTrue();

        state.ToggleSelection(recurring);
        var batchPause = await state.SetSelectedDisabledAsync(true);
        batchPause.IsFailed(out _).Should().BeTrue();

        var runNow = await state.RunRecurringNowAsync(recurring);
        runNow.Status.Should().Be(ResStatus.Ok);
        var persisted = await context.Store.GetDefinitionAsync(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            recurring.Definition.Declaration.JobKey,
            Xunit.TestContext.Current.CancellationToken);
        persisted!.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Catalog_WhenManualModeSuppressesMaterialization_ShouldShowManualOnlyAndKeepActionsDiscoverable()
    {
        await using var context = new JobSchedulerUiTestContext();
        await SetManualSuppressionAsync(context);

        var cut = context.Render<JobCatalogPage>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Catalog:Lifecycle:ManualSuppressed");
            var actions = cut.Find(".catalog-table__actions");
            actions.QuerySelector("[aria-label^='Catalog:Actions:PolicyFor']")!
                .HasAttribute("disabled").Should().BeFalse();
            actions.QuerySelector("[aria-label^='Catalog:Actions:RunFor']")!
                .HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task CatalogState_WhenPolicyAndManualBothSuppressMaterialization_ShouldResumeOnlyOperatorPolicy()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services.GetRequiredService<JobCatalogPageStateFactory>().Create(20);
        await state.InitializeAsync();
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);
        var recurring = state.Summaries.Single();
        (await state.SetDisabledAsync(recurring, true)).Status.Should().Be(ResStatus.Ok);
        await SetManualSuppressionAsync(context);
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobDefinitionSortField.JobName),
            SortDirection = SortDirection.Ascending
        }, Xunit.TestContext.Current.CancellationToken);
        recurring = state.Summaries.Single();
        recurring.SuspensionReasons.Should().Be(
            JobRecurringScheduleSuspensionReason.OperatorPolicy | JobRecurringScheduleSuspensionReason.Manual);

        (await state.SetDisabledAsync(recurring, false)).Status.Should().Be(ResStatus.Ok);

        var optimistic = state.Summaries.Single();
        optimistic.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.Manual);
        optimistic.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.Suspended);
    }

    [Fact]
    public async Task JobDetail_WhenManualModeSuppressesMaterialization_ShouldExplainManualOnlyLifecycle()
    {
        await using var context = new JobSchedulerUiTestContext();
        await SetManualSuppressionAsync(context);

        var cut = context.Render<JobDefinitionDetailPage>(parameters => parameters
            .Add(page => page.OwnerKey, JobSchedulerUiTestContext.OWNER)
            .Add(page => page.JobKey, context.RecurringDefinition.Declaration.JobKey));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Catalog:Policy:Enabled");
            cut.Markup.Should().Contain("Catalog:Lifecycle:ManualSuppressed");
            cut.Markup.Should().Contain("JobDetail:Schedule:ManualOnly");
        });
        var pause = cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Catalog:Actions:Pause", StringComparison.Ordinal));
        pause.HasAttribute("disabled").Should().BeFalse();
        var runNow = cut.FindAll("button").Single(button =>
            button.TextContent.Contains("Catalog:Actions:Run", StringComparison.Ordinal));
        runNow.HasAttribute("disabled").Should().BeFalse();

        pause.Click();

        var persisted = await context.Store.GetDefinitionAsync(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            context.RecurringDefinition.Declaration.JobKey,
            Xunit.TestContext.Current.CancellationToken);
        persisted!.IsDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task JobDetailState_WhenTriggeredPolicyChanges_ShouldRemainNotRecurring()
    {
        await using var context = new JobSchedulerUiTestContext();
        await using var state = context.Services
            .GetRequiredService<JobDefinitionDetailPageStateFactory>()
            .Create(new JobId(JobSchedulerUiTestContext.OWNER, context.TriggeredDefinition.Declaration.JobKey));

        await state.InitializeAsync();
        (await state.SetDisabledAsync(true)).Status.Should().Be(ResStatus.Ok);

        state.Summary.Should().NotBeNull();
        state.Summary!.Definition.IsDisabled.Should().BeTrue();
        state.Summary.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.NotRecurring);
        state.Summary.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.None);
        state.Summary.NextOccurrenceUtc.Should().BeNull();

        (await state.SetDisabledAsync(false)).Status.Should().Be(ResStatus.Ok);
        state.Summary.Definition.IsDisabled.Should().BeFalse();
        state.Summary.RecurringScheduleStatus.Should().Be(JobRecurringScheduleStatus.NotRecurring);
        state.Summary.SuspensionReasons.Should().Be(JobRecurringScheduleSuspensionReason.None);
    }

    [Fact]
    public async Task RecentActivity_WhenDetailsClose_ShouldRefreshTheOwningOverview()
    {
        await using var context = new JobSchedulerUiTestContext();
        var execution = await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "overview-refresh-after-detail-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{}"
        }, Xunit.TestContext.Current.CancellationToken);
        var refreshCount = 0;
        var cut = context.Render<RecentExecutionsPanel>(parameters => parameters
            .Add(component => component.Executions, [execution])
            .Add(component => component.OnRefresh, () => refreshCount++));

        var openTask = cut.Find($"button[aria-label*='{execution.InstanceId}']").ClickAsync();
        context.DialogProvider.WaitForAssertion(() =>
        {
            context.DialogProvider.Markup.Should().Contain(execution.InstanceId);
            refreshCount.Should().Be(0);
        });

        await context.DialogProvider.FindAll("button")
            .Single(button => button.TextContent.Contains("Common:Close", StringComparison.Ordinal))
            .ClickAsync();
        await openTask;

        refreshCount.Should().Be(1);
    }

    [Fact]
    public async Task AnalyticsSlowExecution_ShouldReportTheExactDisplayedCaptureWithoutNavigating()
    {
        await using var context = new JobSchedulerUiTestContext();
        var execution = await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "analytics-slow-execution-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{}"
        }, Xunit.TestContext.Current.CancellationToken);
        var completedAtUtc = new DateTimeOffset(2026, 8, 14, 2, 0, 0, TimeSpan.Zero);
        var snapshot = new JobExecutionAnalyticsSnapshot
        {
            StartTimeUtc = completedAtUtc.AddHours(-1),
            EndTimeUtc = completedAtUtc,
            BucketSize = JobExecutionAnalyticsBucketSize.Hour,
            StateTotals = Enum.GetValues<JobExecutionState>()
                .ToDictionary(static state => state, static _ => 0L),
            Duration = new JobExecutionDurationStatistics(),
            Trend = [],
            TopJobsByVolume = [],
            TopJobsByFailures = [],
            SlowestExecutions =
            [
                new JobExecutionAnalyticsSlowExecution
                {
                    InstanceId = execution.InstanceId,
                    JobName = execution.Template.JobName,
                    OwnerKey = execution.Template.OwnerKey,
                    JobKey = execution.Template.JobKey,
                    State = JobExecutionState.Succeeded,
                    StartedAtUtc = completedAtUtc.AddSeconds(-4),
                    CompletedAtUtc = completedAtUtc
                }
            ]
        };
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var originalUri = navigation.Uri;
        JobExecutionAnalyticsSlowExecution? selected = null;
        var cut = context.Render<SchedulerAnalyticsDurationEvidence>(parameters => parameters
            .Add(component => component.Snapshot, snapshot)
            .Add(component => component.InspectExecution, capture => selected = capture));

        var activity = cut.Find($"button[aria-label*='{execution.InstanceId}']");
        activity.HasAttribute("href").Should().BeFalse();
        activity.GetAttribute("aria-label").Should().Contain(execution.InstanceId);
        activity.GetAttribute("aria-label").Should().Contain("4");
        activity.TextContent.Should().Contain(execution.Template.JobName);
        activity.TextContent.Should().Contain(execution.Template.JobKey);
        await activity.ClickAsync();

        selected.Should().BeSameAs(snapshot.SlowestExecutions.Single());
        navigation.Uri.Should().Be(originalUri);
    }

    [Fact]
    public async Task PolicyWorkbench_ShouldPreserveInvalidCronDraftWithoutMutatingTheCatalog()
    {
        await using var context = new JobSchedulerUiTestContext();
        var summary = (await context.Store.GetOperationalSummaryAsync(
            JobSchedulerUiTestContext.SCOPE,
            new JobId(JobSchedulerUiTestContext.OWNER, context.RecurringDefinition.Declaration.JobKey),
            Xunit.TestContext.Current.CancellationToken))!;
        var dialogService = context.Services.GetRequiredService<IDialogService>();
        await dialogService.ShowAsync<JobPolicyDialog>(
            "Policy",
            new DialogParameters<JobPolicyDialog>
            {
                { dialog => dialog.Definition, context.RecurringDefinition },
                { dialog => dialog.OperationalSummary, summary },
                { dialog => dialog.ObservedAtUtc, DateTimeOffset.UtcNow }
            });

        var provider = context.DialogProvider;
        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Policy:Schedule:CronExpression");
            provider.Markup.Should().Contain("Policy:Schedule:Preview:ConfiguredTime");
            provider.Markup.Should().Contain(context.RecurringDefinition.Declaration.CronExpression);
        });
        provider.Find("input[aria-label='Policy:Schedule:CronExpression']").Input("not-a-cron");
        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Policy:Schedule:ValidationError");
            provider.Find("input[aria-label='Policy:Schedule:CronExpression']")
                .GetAttribute("value").Should().Be("not-a-cron");
        });

        var persisted = await context.Store.GetDefinitionAsync(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            context.RecurringDefinition.Declaration.JobKey,
            Xunit.TestContext.Current.CancellationToken);
        persisted!.Declaration.CronExpression.Should().Be("0 */5 * * * *");
        persisted.Policy.Overrides.ScheduleOverride.Should().BeNull();
    }

    [Fact]
    public async Task PolicyWorkbench_ShouldExposeOperationalFieldsAndKeepTimezoneReadOnly()
    {
        await using var context = new JobSchedulerUiTestContext();

        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<JobPolicyDialog>
        {
            { dialog => dialog.Definition, context.RecurringDefinition }
        };
        await dialogService.ShowAsync<JobPolicyDialog>("Policy", parameters);

        var provider = context.DialogProvider;
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Policy:Schedule:CronExpression"));
        provider.Markup.Should().Contain("Policy:Fields:Enabled");
        provider.Markup.Should().Contain("Policy:Fields:DisplayName");
        provider.Markup.Should().Contain("Policy:Fields:MaxConcurrency");
        provider.Markup.Should().Contain("Policy:Fields:RetryCount");
        provider.Markup.Should().Contain("Policy:Fields:ExecutionTimeout");
        provider.Markup.Should().Contain("Policy:Fields:MaxRecords");
        provider.Markup.Should().Contain("Policy:Fields:MaxDays");
        provider.Find(".schedule-editor__timezone code").TextContent.Should().Be("UTC");
        provider.FindAll(".schedule-editor__timezone input, .schedule-editor__timezone button, .schedule-editor__timezone [role='combobox']")
            .Should().BeEmpty();
    }

    [Fact]
    public async Task PolicyWorkbench_WhenEffectivePolicyIsPaused_ShouldSuppressProspectiveOccurrences()
    {
        await using var context = new JobSchedulerUiTestContext();
        await context.Store.UpdatePolicyAsync(
            JobSchedulerUiTestContext.SCOPE,
            context.RecurringDefinition.OwnerKey,
            context.RecurringDefinition.Declaration.JobKey,
            new()
            {
                Overrides = context.RecurringDefinition.Policy.Overrides with { DisabledOverride = true },
                ExpectedConcurrencyStamp = context.RecurringDefinition.Policy.ConcurrencyStamp
            },
            Xunit.TestContext.Current.CancellationToken);
        var summary = (await context.Store.GetOperationalSummaryAsync(
            JobSchedulerUiTestContext.SCOPE,
            new JobId(JobSchedulerUiTestContext.OWNER, context.RecurringDefinition.Declaration.JobKey),
            Xunit.TestContext.Current.CancellationToken))!;
        var dialogService = context.Services.GetRequiredService<IDialogService>();

        await dialogService.ShowAsync<JobPolicyDialog>(
            "Policy",
            new DialogParameters<JobPolicyDialog>
            {
                { dialog => dialog.Definition, summary.Definition },
                { dialog => dialog.OperationalSummary, summary }
            });

        var provider = context.DialogProvider;
        provider.WaitForAssertion(() =>
        {
            provider.Find(".schedule-editor__prospective").TextContent.Should()
                .Contain("Policy:Schedule:PreviewSuspended");
            var preview = provider.Find(".schedule-editor__preview-scroll tbody");
            preview.TextContent.Should().Contain("Policy:Schedule:PreviewSuspendedDescription");
            preview.QuerySelectorAll("code").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task PolicyDialog_WhenAccessIsRevokedBeforeSave_ShouldRejectTheMutation()
    {
        await using var context = new JobSchedulerUiTestContext();
        var dialogService = context.Services.GetRequiredService<IDialogService>();
        await dialogService.ShowAsync<JobPolicyDialog>(
            "Policy",
            new DialogParameters<JobPolicyDialog>
            {
                { dialog => dialog.Definition, context.TriggeredDefinition }
            });
        context.DialogProvider.WaitForElement("input[aria-label='Policy:Fields:DisplayName']")
            .Input("Operator draft");
        context.Access.IsAuthorized = false;

        var provider = context.DialogProvider;
        provider.WaitForElement("button");
        provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Common:Save", StringComparison.Ordinal))
            .Click();

        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Access:DeniedDescription"));
        var definition = await context.Store.GetDefinitionAsync(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            context.TriggeredDefinition.Declaration.JobKey,
            Xunit.TestContext.Current.CancellationToken);
        definition!.Policy.ConcurrencyStamp.Should().Be(context.TriggeredDefinition.Policy.ConcurrencyStamp);
    }

    [Fact]
    public async Task PolicyDialog_WhenClosedDuringAuthorization_ShouldCancelAndJoinSaveWithoutMutation()
    {
        await using var context = new JobSchedulerUiTestContext();
        context.Access.BlockAuthorization();
        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var dialog = await dialogService.ShowAsync<JobPolicyDialog>(
            "Policy",
            new DialogParameters<JobPolicyDialog>
            {
                { policyDialog => policyDialog.Definition, context.TriggeredDefinition }
            });
        var provider = context.DialogProvider;
        provider.WaitForElement("input[aria-label='Policy:Fields:DisplayName']")
            .Input("Operator draft");
        var save = provider.FindAll("button")
            .Single(button => button.TextContent.Contains("Common:Save", StringComparison.Ordinal));

        var saveTask = save.ClickAsync();
        await context.Access.AuthorizationStarted.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        await provider.InvokeAsync(() => dialog.Close());
        await saveTask.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        provider.WaitForAssertion(() => provider.FindComponents<JobPolicyDialog>().Should().BeEmpty());

        var definition = await context.Store.GetDefinitionAsync(
            JobSchedulerUiTestContext.SCOPE,
            JobSchedulerUiTestContext.OWNER,
            context.TriggeredDefinition.Declaration.JobKey,
            Xunit.TestContext.Current.CancellationToken);
        definition!.Policy.ConcurrencyStamp.Should().Be(context.TriggeredDefinition.Policy.ConcurrencyStamp);
        definition.Policy.Overrides.DisplayNameOverride.Should().BeNull();
    }

    [Fact]
    public async Task ExecutionDetail_ShouldRenderTimingPolicyLeaseHistoryAndDiagnosticActions()
    {
        await using var context = new JobSchedulerUiTestContext();
        var execution = await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "execution-detail-test-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{\"report\":\"daily\"}",
            AvailableAtUtc = DateTimeOffset.UtcNow
        }, Xunit.TestContext.Current.CancellationToken);
        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        await dialogService.ShowAsync<ExecutionDetailDialog>(
            "Execution",
            new DialogParameters<ExecutionDetailDialog>
            {
                { dialog => dialog.InstanceId, execution.InstanceId }
            });

        var provider = context.DialogProvider;
        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain(execution.InstanceId);
            provider.Find(".execution-detail__viewport").Should().NotBeNull();
            provider.Find(".execution-detail-hero").Should().NotBeNull();
            provider.FindAll(".execution-evidence__panel").Should().HaveCount(5);
            provider.Markup.Should().Contain("ExecutionDetail:PolicySnapshot");
            provider.Markup.Should().Contain("ExecutionDetail:Timing");
            provider.Markup.Should().Contain("ExecutionDetail:LeaseLosses");
            provider.Markup.Should().Contain("ExecutionDetail:Actions:CopySnapshot");
        });

        await provider.FindAll("[role='tab']")
            .Single(tab => tab.TextContent.Contains("ExecutionDetail:Tabs:History", StringComparison.Ordinal))
            .ClickAsync();
        var stored = await context.Store.GetExecutionAsync(JobSchedulerUiTestContext.SCOPE,
            execution.InstanceId, Xunit.TestContext.Current.CancellationToken);
        provider.FindComponent<ExecutionHistoryLedger>().Instance.Entries.Should().BeEquivalentTo(stored!.History);
        await provider.Find("button[aria-label='ExecutionDetail:Actions:More']").ClickAsync();
        await context.PopoverProvider.FindAll("[role='menuitem']")
            .Single(button => button.TextContent.Contains("ExecutionDetail:Actions:ViewCatalog", StringComparison.Ordinal))
            .ClickAsync();
        navigation.Uri.Should().EndWith(
            $"/job-scheduler/catalog/{Uri.EscapeDataString(execution.Template.OwnerKey)}/{Uri.EscapeDataString(execution.Template.JobKey)}");
    }

    [Fact]
    public async Task ExecutionLedgerState_WhenDisposedDuringAuthorization_ShouldCancelAndJoinTheLoad()
    {
        await using var context = new JobSchedulerUiTestContext();
        context.Access.BlockAuthorization();
        var state = context.Services.GetRequiredService<JobExecutionsStateFactory>().CreatePageState();

        var load = state.InitializeAsync();
        await context.Access.AuthorizationStarted.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        await state.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        state.IsDisposed.Should().BeTrue();
        state.Executions.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecutionsDeepLink_ShouldApplyStatesAndRequestedInstanceFilter()
    {
        await using var context = new JobSchedulerUiTestContext();
        var execution = await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "execution-deep-link-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{}",
            AvailableAtUtc = DateTimeOffset.UtcNow
        }, Xunit.TestContext.Current.CancellationToken);

        await using var state = context.Services.GetRequiredService<JobExecutionsStateFactory>().CreatePageState();
        state.ApplyInitialQuery("queued,Running,invalid", execution.InstanceId);
        await state.InitializeAsync();
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobExecutionSortField.CreatedAtUtc),
            SortDirection = SortDirection.Descending
        }, Xunit.TestContext.Current.CancellationToken);

        state.SelectedStates.Should().BeEquivalentTo(
            [JobExecutionState.Queued, JobExecutionState.Running]);
        state.SearchText.Should().Be(execution.InstanceId);
        state.Executions.Should().ContainSingle(item => item.InstanceId == execution.InstanceId);
    }

    [Fact]
    public async Task ExecutionCancellation_ShouldReauthorizeBeforePersistingAndReloadThroughTableState()
    {
        await using var context = new JobSchedulerUiTestContext();
        var execution = await context.Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
            InstanceId = "execution-cancel-test-0001",
            JobKey = context.TriggeredDefinition.Declaration.JobKey,
            OwnerKey = context.TriggeredDefinition.OwnerKey,
            JobArgs = "{}",
            AvailableAtUtc = DateTimeOffset.UtcNow
        }, Xunit.TestContext.Current.CancellationToken);
        await using var state = context.Services.GetRequiredService<JobExecutionsStateFactory>().CreatePageState();
        await state.InitializeAsync();

        context.Access.IsAuthorized = false;
        var denied = await state.RequestCancellationAsync(execution.InstanceId);
        denied.IsAuthorized.Should().BeFalse();
        var unchanged = await context.Store.GetExecutionAsync(
            JobSchedulerUiTestContext.SCOPE,
            execution.InstanceId,
            Xunit.TestContext.Current.CancellationToken);
        unchanged!.State.Should().Be(JobExecutionState.Queued);

        context.Access.IsAuthorized = true;
        var applied = await state.RequestCancellationAsync(execution.InstanceId);
        applied.Status.Should().Be(JobCancellationStatus.Cancelled);
        await state.LoadTableAsync(new TableState
        {
            PageSize = 20,
            SortLabel = nameof(JobExecutionSortField.CreatedAtUtc),
            SortDirection = SortDirection.Descending
        }, Xunit.TestContext.Current.CancellationToken);
        state.Executions.Should().ContainSingle(item =>
            item.InstanceId == execution.InstanceId && item.State == JobExecutionState.Cancelled);
    }

    private static async Task SetManualSuppressionAsync(JobSchedulerUiTestContext context)
    {
        await context.Store.SynchronizeRecurringScheduleAsync(
            new RecurringScheduleSynchronization
            {
                CursorKey = new RecurringScheduleCursorKey
                {
                    SchedulerScopeKey = JobSchedulerUiTestContext.SCOPE,
                    OwnerKey = JobSchedulerUiTestContext.OWNER,
                    JobKey = context.RecurringDefinition.Declaration.JobKey
                },
                HostSuspensionReasons = JobRecurringScheduleSuspensionReason.Manual
            },
            Xunit.TestContext.Current.CancellationToken);
    }
}
