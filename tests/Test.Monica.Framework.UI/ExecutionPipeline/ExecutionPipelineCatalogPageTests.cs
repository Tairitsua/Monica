using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution.Models;
using Monica.Core.Modularity.Models;
using Monica.Framework.UI.Pages;
using Monica.Framework.UI.UIExecutionPipeline.Components;
using Monica.Framework.UI.UIExecutionPipeline.State;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.UI.ExecutionPipeline;

public sealed class ExecutionPipelineCatalogPageTests
{
    [Fact]
    public async Task Empty_observed_catalog_explains_when_exact_plans_appear()
    {
        var snapshot = ExecutionPipelineUiTestContext.CreateSnapshot(
            registrations: [ExecutionPipelineUiTestContext.CreateRegistration("LoggingBehavior", 100)]);
        await using var context = new ExecutionPipelineUiTestContext(snapshot);

        var cut = context.Render<UIExecutionPipelinePage>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Pipeline:NoPlansTitle").And.Contain("Pipeline:NoPlans");
        });
    }

    [Fact]
    public async Task Page_disposal_is_idempotent_after_state_notifications()
    {
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot());
        var cut = context.Render<UIExecutionPipelinePage>();

        cut.WaitForAssertion(() =>
            cut.Markup.Should().Contain("Pipeline:DisplayLabel"));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Plan_list_exposes_operation_name_and_optional_mobile_chevron_separately()
    {
        var plan = ExecutionPipelineUiTestContext.CreatePlan("OperationWithALongReadableName");
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(plans: [plan]));

        var cut = context.Render<ExecutionPipelinePlanList>(parameters => parameters
            .Add(component => component.Plans, [plan]));

        cut.Find(".execution-pipeline-plan-list__content strong").TextContent.Should()
            .Be("Example.Component.OperationWithALongReadableName");
        cut.Find(".execution-pipeline-plan-list__chevron").GetAttribute("aria-hidden").Should().Be("true");
    }

    [Fact]
    public async Task Ready_plan_renders_applied_behaviors_in_outer_to_inner_order()
    {
        var plan = ExecutionPipelineUiTestContext.CreatePlan(
            "ApproveOrder",
            behaviors:
            [
                ExecutionPipelineUiTestContext.CreateBehavior(1, "AuthorizationBehavior", -1000),
                ExecutionPipelineUiTestContext.CreateBehavior(2, "TimingBehavior", 1000)
            ]);
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(plans: [plan]));

        var cut = context.Render<ExecutionPipelinePlanDetail>(parameters => parameters
            .Add(component => component.Plan, plan));

        var behaviorRows = cut.FindAll("[data-testid='execution-pipeline-applied-behavior']");
        behaviorRows.Should().HaveCount(2);
        behaviorRows[0].TextContent.Should().Contain("AuthorizationBehavior");
        behaviorRows[0].TextContent.Should().Contain("-1000");
        behaviorRows[1].TextContent.Should().Contain("TimingBehavior");
        behaviorRows[1].TextContent.Should().Contain("1000");
    }

    [Fact]
    public async Task Faulted_plan_renders_cached_failure_details()
    {
        var plan = ExecutionPipelineUiTestContext.CreatePlan(
            "FaultedOperation",
            ExecutionPipelinePlanStatus.Faulted,
            error: new ExecutionPipelinePlanErrorSnapshot(
                "System.InvalidOperationException",
                "Descriptor filter rejected the operation."));
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(plans: [plan]));

        var cut = context.Render<ExecutionPipelinePlanDetail>(parameters => parameters
            .Add(component => component.Plan, plan));

        var fault = cut.Find("[data-testid='execution-pipeline-plan-error']");
        fault.TextContent.Should().Contain("System.InvalidOperationException");
        fault.TextContent.Should().Contain("Descriptor filter rejected the operation.");
        cut.FindAll("[data-testid='execution-pipeline-zero-behaviors']").Should().BeEmpty();
    }

    [Fact]
    public async Task Ready_plan_without_behaviors_renders_terminal_fast_path_state()
    {
        var plan = ExecutionPipelineUiTestContext.CreatePlan("DirectOperation");
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(plans: [plan]));

        var cut = context.Render<ExecutionPipelinePlanDetail>(parameters => parameters
            .Add(component => component.Plan, plan));

        cut.Find("[data-testid='execution-pipeline-zero-behaviors']").Should().NotBeNull();
        cut.Markup.Should().Contain("Page:States:NoAppliedBehaviors");
    }

    [Fact]
    public async Task Registration_table_renders_order_lifetime_source_and_filter_metadata()
    {
        var registration = ExecutionPipelineUiTestContext.CreateRegistration("AuthorizationBehavior", -1000);
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(registrations: [registration]));

        var cut = context.Render<ExecutionPipelineRegistrationTable>(parameters => parameters
            .Add(component => component.Registrations, [registration]));

        var row = cut.Find("[data-testid='execution-pipeline-registration-row']");
        row.TextContent.Should().Contain("AuthorizationBehavior");
        cut.Markup.Should().NotContain("Version=1.0.0.0");
        cut.FindAll("[data-testid='execution-pipeline-help-label']").Should().HaveCountGreaterThanOrEqualTo(6);
        cut.Markup.Should().Contain("-1000");
        cut.Markup.Should().Contain("Page:Lifetimes:Scoped");
        cut.Markup.Should().Contain(ModuleKey.FromModuleType(typeof(ModuleMediator)).ToString());
        cut.Markup.Should().Contain("Page:Common:Yes");
    }

    [Fact]
    public async Task Search_matches_applied_behavior_and_source_module_names()
    {
        var plan = ExecutionPipelineUiTestContext.CreatePlan(
            "ApproveOrder",
            behaviors: [ExecutionPipelineUiTestContext.CreateBehavior(1, "AuthorizationBehavior", -1000)]);
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(plans: [plan]));
        var state = context.Services.GetRequiredService<ExecutionPipelinePageState>();
        (await state.InitializeAsync()).Should().BeTrue();

        state.SetSearchText("AuthorizationBehavior");
        state.FilteredPlans.Should().ContainSingle().Which.Id.Should().Be(plan.Id);

        state.SetSearchText("Mediator");
        state.FilteredPlans.Should().ContainSingle().Which.Id.Should().Be(plan.Id);

        state.SetSearchText("missing-behavior");
        state.FilteredPlans.Should().BeEmpty();
    }

    [Fact]
    public async Task Plan_detail_keeps_canonical_identities_out_of_the_primary_reading_flow()
    {
        var plan = ExecutionPipelineUiTestContext.CreatePlan("ApproveOrder");
        await using var context = new ExecutionPipelineUiTestContext(
            ExecutionPipelineUiTestContext.CreateSnapshot(plans: [plan]));

        var cut = context.Render<ExecutionPipelinePlanDetail>(parameters => parameters
            .Add(component => component.Plan, plan));

        cut.Find("[data-testid='execution-pipeline-plan-overview']").Should().NotBeNull();
        cut.Markup.Should().Contain("Page:Diagnostics:Title");
        cut.Markup.Should().NotContain(plan.Diagnostics.CanonicalKey);
        cut.Markup.Should().NotContain("Version=1.0.0.0");
    }
}
