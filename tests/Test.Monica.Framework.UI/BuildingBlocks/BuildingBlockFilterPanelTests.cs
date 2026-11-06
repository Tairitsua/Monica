using AwesomeAssertions;
using Bunit;
using Monica.Core.Results;
using Monica.Framework.UI.UIBuildingBlocks.Components;
using Monica.BuildingBlocks.Models;
using MudBlazor;
using Xunit;

namespace Test.Monica.Framework.UI.BuildingBlocks;

public sealed class BuildingBlockFilterPanelTests
{
    [Fact]
    public async Task Type_filters_show_only_discovered_types_with_catalog_counts()
    {
        BuildingBlockSummary[] units =
        [
            CreateUnit("First service", BuildingBlockKind.ApplicationService),
            CreateUnit("Second service", BuildingBlockKind.ApplicationService),
            CreateUnit("Order", BuildingBlockKind.Entity)
        ];
        var selectedTypes = units.Select(static unit => unit.Kind).ToHashSet();
        await using var context = new BuildingBlocksUiTestContext(
            new StubBuildingBlocksUiDataSource(Result.Failure<BuildingBlockDashboardSnapshot>("unused")));

        var cut = context.Render<BuildingBlockFilterPanel>(parameters => parameters
            .Add(component => component.BuildingBlocks, units)
            .Add(component => component.SelectedKinds, selectedTypes));

        var chips = cut.FindComponents<MudChip<BuildingBlockKind>>();
        chips.Should().HaveCount(2);
        chips.Select(static chip => chip.Instance.Value).Should().BeEquivalentTo(
            [BuildingBlockKind.ApplicationService, BuildingBlockKind.Entity]);
        chips.Single(chip => chip.Instance.Value == BuildingBlockKind.ApplicationService)
            .Find(".mud-chip-content")
            .TextContent
            .Should()
            .Contain("2");
    }

    private static BuildingBlockSummary CreateUnit(string title, BuildingBlockKind kind)
    {
        return new BuildingBlockSummary
        {
            Key = $"Test.{title.Replace(' ', '.')}",
            Title = title,
            Kind = kind
        };
    }
}
