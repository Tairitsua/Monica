using AwesomeAssertions;
using Bunit;
using Monica.Core.Results;
using Monica.Framework.UI.UIProjectUnits.Components;
using Monica.Framework.UI.UIProjectUnits.Dialogs;
using Monica.ProjectUnits.Models;
using Xunit;

namespace Test.Monica.Framework.UI.ProjectUnits;

public sealed class ProjectUnitDashboardTests
{
    [Fact]
    public async Task Populated_dashboard_renders_service_metrics_distribution_and_gaps()
    {
        var summary = CreateSummary();
        var snapshot = CreateSnapshot(summary);
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Res.Ok(snapshot)));

        var cut = context.Render<ProjectUnitDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='project-unit-dashboard-metrics']").Should().NotBeNull();
            cut.Find("[data-testid='project-unit-type-distribution']").Should().NotBeNull();
            cut.Find("[data-testid='project-unit-coverage-gaps']").Should().NotBeNull();
            cut.Find("[data-testid='project-unit-service-project-name']").Should().NotBeNull();
            cut.Find("[data-testid='project-unit-service-app-id']").Should().NotBeNull();
            cut.Markup.Should().Contain("Ordering Service");
            cut.Markup.Should().Contain("Approve Order");
        });
    }

    [Fact]
    public async Task Fallback_service_identities_are_not_rendered_as_duplicate_values()
    {
        var snapshot = CreateSnapshot(service: new ProjectUnitServiceIdentity
        {
            ProjectName = "FlightService.API",
            AppId = "FlightService.API",
            AppName = "FlightService.API",
            AppVersion = "1.0.3"
        });
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Res.Ok(snapshot)));

        var cut = context.Render<ProjectUnitDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='project-unit-service-project-name']").Should().BeEmpty();
            cut.FindAll("[data-testid='project-unit-service-app-id']").Should().BeEmpty();
            cut.Markup.Should().NotContain("ProjectUnitDashboard:Service:ProjectName");
            cut.Markup.Should().NotContain("ProjectUnitDashboard:Service:AppId");
        });
    }

    [Fact]
    public async Task Long_operational_identifiers_remain_available_to_responsive_surfaces()
    {
        const string version = "1.0.0-rc.12+01be8bf141939bf820187df9f2fd2775313434e7";
        const string key = "Ordering.Application.Commands.Handlers.ApproveOrderWithAnUninterruptedTechnicalIdentity";
        var summary = CreateSummary(key);
        var snapshot = CreateSnapshot(
            summary,
            new ProjectUnitServiceIdentity
            {
                ProjectName = "Ordering.Api",
                AppId = "ordering-api",
                AppName = "Ordering Service",
                AppVersion = version,
                DomainName = "Ordering"
            });
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Res.Ok(snapshot)));

        var cut = context.Render<ProjectUnitDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='project-unit-service-project-name']").ParentElement!
                .ParentElement!.TextContent.Should().Contain(version);
            var gap = cut.Find($"[data-unit-key='{key}']");
            gap.QuerySelector("code")!.TextContent.Should().Be(key);
            gap.QuerySelector("button[aria-label]").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Empty_dashboard_renders_no_data_state_and_never_full_coverage()
    {
        var baseline = CreateSnapshot();
        var snapshot = new ProjectUnitDashboardSnapshot
        {
            Service = baseline.Service,
            TotalUnits = 0,
            DistinctUnitTypes = 0,
            Coverage = CreateCoverage(total: 0, covered: 0, percentage: null),
            UnitTypes = [],
            Dependencies = new ProjectUnitDependencyStatistics(),
            Alerts = new ProjectUnitAlertStatistics(),
            CoverageGaps = []
        };
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Res.Ok(snapshot)));

        var cut = context.Render<ProjectUnitDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='project-unit-dashboard-empty']").Should().NotBeNull();
            cut.Find("[data-testid='project-unit-coverage']").TextContent.Should().Contain("--");
            cut.FindAll("[data-testid='project-unit-coverage'] [role='progressbar']")
                .Should().OnlyContain(bar => bar.GetAttribute("aria-valuenow") == "0");
        });
    }

    [Fact]
    public async Task Failed_dashboard_renders_error_and_retry_action()
    {
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Res.Fail("catalog unavailable")));

        var cut = context.Render<ProjectUnitDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='project-unit-dashboard-error']").Should().NotBeNull();
            cut.Markup.Should().Contain("catalog unavailable");
            cut.Markup.Should().Contain("ProjectUnitDashboard:Actions:Retry");
        });
    }

    [Fact]
    public async Task Detail_dialog_renders_resolved_links_safely_and_keeps_unresolved_ids_visible()
    {
        var summary = CreateSummary();
        var detail = new ProjectUnitDetail
        {
            Summary = summary,
            Requirements =
            [
                new ProjectUnitRequirementReference
                {
                    Id = "ORD-REQ-001",
                    Title = "Order approval requirement",
                    Href = "/workflow/requirements/ORD-REQ-001"
                },
                new ProjectUnitRequirementReference
                {
                    Id = "ORD-REQ-002",
                    Title = "ORD-REQ-002"
                }
            ]
        };
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Res.Ok(CreateSnapshot(summary)), Res.Ok(detail)));

        var cut = context.Render<ProjectUnitDetailDialog>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.SelectedUnit, summary));

        context.DialogProvider.WaitForAssertion(() =>
        {
            var link = context.DialogProvider.Find("a[href='/workflow/requirements/ORD-REQ-001']");
            link.GetAttribute("target").Should().Be("_blank");
            link.GetAttribute("rel").Should().Be("noopener noreferrer");
            context.DialogProvider.Markup.Should().Contain("ORD-REQ-002");
            context.DialogProvider.FindAll("[data-resolved='false']").Should().ContainSingle();
        });
    }

    private static ProjectUnitDashboardSnapshot CreateSnapshot(
        ProjectUnitSummary? summary = null,
        ProjectUnitServiceIdentity? service = null)
    {
        return new ProjectUnitDashboardSnapshot
        {
            Service = service ?? new ProjectUnitServiceIdentity
            {
                ProjectName = "Ordering.Api",
                AppId = "ordering-api",
                AppName = "Ordering Service",
                AppVersion = "2.3.0",
                DomainName = "Ordering"
            },
            TotalUnits = summary is null ? 1 : 2,
            DistinctUnitTypes = 1,
            Coverage = CreateCoverage(total: 2, covered: 1, percentage: 50m),
            UnitTypes =
            [
                new ProjectUnitTypeStatistics
                {
                    UnitType = EProjectUnitType.ApplicationService,
                    Count = 2,
                    Percentage = 100m
                }
            ],
            Dependencies = new ProjectUnitDependencyStatistics
            {
                EdgeCount = 1,
                IsolatedUnitCount = 0
            },
            Alerts = new ProjectUnitAlertStatistics
            {
                WarningCount = summary is null ? 0 : 1
            },
            CoverageGaps = summary is null
                ? []
                :
                [
                    new ProjectUnitCoverageGap
                    {
                        Unit = summary,
                        MissingCoverage = [ProjectUnitCoverageKind.Ownership]
                    }
                ]
        };
    }

    private static ProjectUnitSummary CreateSummary(
        string key = "Ordering.Application.CommandApproveOrder")
    {
        return new ProjectUnitSummary
        {
            Key = key,
            Title = "Approve Order",
            Description = "Approves an eligible order.",
            UnitType = EProjectUnitType.ApplicationService,
            HasExplicitMetadata = true,
            RequirementCount = 2,
            MethodCount = 1,
            Alerts =
            [
                new ProjectUnitAlert
                {
                    Level = EAlertLevel.Warning,
                    Message = "Owner is missing."
                }
            ]
        };
    }

    private static IReadOnlyList<ProjectUnitCoverageMetric> CreateCoverage(
        int total,
        int covered,
        decimal? percentage)
    {
        return Enum.GetValues<ProjectUnitCoverageKind>()
            .Select(kind => new ProjectUnitCoverageMetric
            {
                Kind = kind,
                Total = total,
                Covered = covered,
                Percentage = percentage
            })
            .ToList();
    }
}
