using AwesomeAssertions;
using Bunit;
using Monica.Core.Results;
using Monica.Framework.UI.UIBuildingBlocks.Components;
using Monica.Framework.UI.UIBuildingBlocks.Dialogs;
using Monica.BuildingBlocks.Models;
using Xunit;

namespace Test.Monica.Framework.UI.BuildingBlocks;

public sealed class BuildingBlockDashboardTests
{
    [Fact]
    public async Task Populated_dashboard_renders_service_metrics_distribution_and_gaps()
    {
        var summary = CreateSummary();
        var snapshot = CreateSnapshot(summary);
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Success(snapshot)));

        var cut = context.Render<BuildingBlockDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='building-block-dashboard-metrics']").Should().NotBeNull();
            cut.Find("[data-testid='building-block-type-distribution']").Should().NotBeNull();
            cut.Find("[data-testid='building-block-coverage-gaps']").Should().NotBeNull();
            cut.Find("[data-testid='building-block-service-project-name']").Should().NotBeNull();
            cut.Find("[data-testid='building-block-service-app-id']").Should().NotBeNull();
            cut.Markup.Should().Contain("Ordering Service");
            cut.Markup.Should().Contain("Approve Order");
        });
    }

    [Fact]
    public async Task Fallback_service_identities_are_not_rendered_as_duplicate_values()
    {
        var snapshot = CreateSnapshot(service: new BuildingBlockServiceIdentity
        {
            ProjectName = "FlightService.API",
            AppId = "FlightService.API",
            AppName = "FlightService.API",
            AppVersion = "1.0.3"
        });
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Success(snapshot)));

        var cut = context.Render<BuildingBlockDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='building-block-service-project-name']").Should().BeEmpty();
            cut.FindAll("[data-testid='building-block-service-app-id']").Should().BeEmpty();
            cut.Markup.Should().NotContain("BuildingBlockDashboard:Service:ProjectName");
            cut.Markup.Should().NotContain("BuildingBlockDashboard:Service:AppId");
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
            new BuildingBlockServiceIdentity
            {
                ProjectName = "Ordering.Api",
                AppId = "ordering-api",
                AppName = "Ordering Service",
                AppVersion = version,
                DomainName = "Ordering"
            });
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Success(snapshot)));

        var cut = context.Render<BuildingBlockDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='building-block-service-project-name']").ParentElement!
                .ParentElement!.TextContent.Should().Contain(version);
            var gap = cut.Find($"[data-building-block-key='{key}']");
            gap.QuerySelector("code")!.TextContent.Should().Be(key);
            gap.QuerySelector("button[aria-label]").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Empty_dashboard_renders_no_data_state_and_never_full_coverage()
    {
        var baseline = CreateSnapshot();
        var snapshot = new BuildingBlockDashboardSnapshot
        {
            Service = baseline.Service,
            TotalBuildingBlocks = 0,
            DistinctKinds = 0,
            Coverage = CreateCoverage(total: 0, covered: 0, percentage: null),
            Kinds = [],
            Dependencies = new BuildingBlockDependencyStatistics(),
            Alerts = new BuildingBlockAlertStatistics(),
            CoverageGaps = []
        };
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Success(snapshot)));

        var cut = context.Render<BuildingBlockDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='building-block-dashboard-empty']").Should().NotBeNull();
            cut.Find("[data-testid='building-block-coverage']").TextContent.Should().Contain("--");
            cut.FindAll("[data-testid='building-block-coverage'] [role='progressbar']")
                .Should().OnlyContain(bar => bar.GetAttribute("aria-valuenow") == "0");
        });
    }

    [Fact]
    public async Task Failed_dashboard_renders_error_and_retry_action()
    {
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Failure<BuildingBlockDashboardSnapshot>("catalog unavailable")));

        var cut = context.Render<BuildingBlockDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='building-block-dashboard-error']").Should().NotBeNull();
            cut.Markup.Should().Contain("catalog unavailable");
            cut.Markup.Should().Contain("BuildingBlockDashboard:Actions:Retry");
        });
    }

    [Fact]
    public async Task Detail_dialog_renders_resolved_links_safely_and_keeps_unresolved_ids_visible()
    {
        var summary = CreateSummary();
        var detail = new BuildingBlockDetail
        {
            Summary = summary,
            Requirements =
            [
                new BuildingBlockRequirementReference
                {
                    Id = "ORD-REQ-001",
                    Title = "Order approval requirement",
                    Href = "/workflow/requirements/ORD-REQ-001"
                },
                new BuildingBlockRequirementReference
                {
                    Id = "ORD-REQ-002",
                    Title = "ORD-REQ-002"
                }
            ]
        };
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Success(CreateSnapshot(summary)), Result.Success(detail)));

        var cut = context.Render<BuildingBlockDetailDialog>(parameters => parameters
            .Add(component => component.Visible, true)
            .Add(component => component.SelectedBuildingBlock, summary));

        context.DialogProvider.WaitForAssertion(() =>
        {
            var link = context.DialogProvider.Find("a[href='/workflow/requirements/ORD-REQ-001']");
            link.GetAttribute("target").Should().Be("_blank");
            link.GetAttribute("rel").Should().Be("noopener noreferrer");
            context.DialogProvider.Markup.Should().Contain("ORD-REQ-002");
            context.DialogProvider.FindAll("[data-resolved='false']").Should().ContainSingle();
        });
    }

    private static BuildingBlockDashboardSnapshot CreateSnapshot(
        BuildingBlockSummary? summary = null,
        BuildingBlockServiceIdentity? service = null)
    {
        return new BuildingBlockDashboardSnapshot
        {
            Service = service ?? new BuildingBlockServiceIdentity
            {
                ProjectName = "Ordering.Api",
                AppId = "ordering-api",
                AppName = "Ordering Service",
                AppVersion = "2.3.0",
                DomainName = "Ordering"
            },
            TotalBuildingBlocks = summary is null ? 1 : 2,
            DistinctKinds = 1,
            Coverage = CreateCoverage(total: 2, covered: 1, percentage: 50m),
            Kinds =
            [
                new BuildingBlockKindStatistics
                {
                    Kind = BuildingBlockKind.ApplicationService,
                    Count = 2,
                    Percentage = 100m
                }
            ],
            Dependencies = new BuildingBlockDependencyStatistics
            {
                EdgeCount = 1,
                IsolatedBuildingBlockCount = 0
            },
            Alerts = new BuildingBlockAlertStatistics
            {
                WarningCount = summary is null ? 0 : 1
            },
            CoverageGaps = summary is null
                ? []
                :
                [
                    new BuildingBlockCoverageGap
                    {
                        BuildingBlock = summary,
                        MissingCoverage = [BuildingBlockCoverageKind.Ownership]
                    }
                ]
        };
    }

    private static BuildingBlockSummary CreateSummary(
        string key = "Ordering.Application.CommandApproveOrder")
    {
        return new BuildingBlockSummary
        {
            Key = key,
            Title = "Approve Order",
            Description = "Approves an eligible order.",
            Kind = BuildingBlockKind.ApplicationService,
            HasExplicitMetadata = true,
            RequirementCount = 2,
            MethodCount = 1,
            Alerts =
            [
                new BuildingBlockAlert
                {
                    Level = EAlertLevel.Warning,
                    Message = "Owner is missing."
                }
            ]
        };
    }

    private static IReadOnlyList<BuildingBlockCoverageMetric> CreateCoverage(
        int total,
        int covered,
        decimal? percentage)
    {
        return Enum.GetValues<BuildingBlockCoverageKind>()
            .Select(kind => new BuildingBlockCoverageMetric
            {
                Kind = kind,
                Total = total,
                Covered = covered,
                Percentage = percentage
            })
            .ToList();
    }
}
