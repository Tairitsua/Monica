using AwesomeAssertions;
using Bunit;
using Monica.Core.Results;
using Monica.Framework.UI.UIProjectUnits.Components;
using Monica.ProjectUnits.Models;
using MudBlazor;
using Xunit;

namespace Test.Monica.Framework.UI.ProjectUnits;

public sealed class ProjectUnitFilterPanelTests
{
    [Fact]
    public async Task Type_filters_show_only_discovered_types_with_catalog_counts()
    {
        ProjectUnitSummary[] units =
        [
            CreateUnit("First service", EProjectUnitType.ApplicationService),
            CreateUnit("Second service", EProjectUnitType.ApplicationService),
            CreateUnit("Order", EProjectUnitType.Entity)
        ];
        var selectedTypes = units.Select(static unit => unit.UnitType).ToHashSet();
        await using var context = new ProjectUnitsUiTestContext(
            new StubProjectUnitsUiDataSource(Result.Failure<ProjectUnitDashboardSnapshot>("unused")));

        var cut = context.Render<ProjectUnitFilterPanel>(parameters => parameters
            .Add(component => component.Units, units)
            .Add(component => component.SelectedUnitTypes, selectedTypes));

        var chips = cut.FindComponents<MudChip<EProjectUnitType>>();
        chips.Should().HaveCount(2);
        chips.Select(static chip => chip.Instance.Value).Should().BeEquivalentTo(
            [EProjectUnitType.ApplicationService, EProjectUnitType.Entity]);
        chips.Single(chip => chip.Instance.Value == EProjectUnitType.ApplicationService)
            .Find(".mud-chip-content")
            .TextContent
            .Should()
            .Contain("2");
    }

    private static ProjectUnitSummary CreateUnit(string title, EProjectUnitType unitType)
    {
        return new ProjectUnitSummary
        {
            Key = $"Test.{title.Replace(' ', '.')}",
            Title = title,
            UnitType = unitType
        };
    }
}
