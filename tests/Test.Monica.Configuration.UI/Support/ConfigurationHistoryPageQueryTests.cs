using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Pages;
using Monica.Configuration.UI.Support;

namespace Test.Monica.Configuration.UI.Support;

public sealed class ConfigurationHistoryPageQueryTests
{
    [Theory]
    [InlineData("all", null)]
    [InlineData("monica", ConfigurationMutationTargetKind.MonicaEffectiveStore)]
    [InlineData("external", ConfigurationMutationTargetKind.ExternalConfigurationSource)]
    public void CreateRequests_WhenFiltersAreAdmitted_ShouldPreserveExactIdentityAndScopeTargetsToHistory(
        string target, ConfigurationMutationTargetKind? expectedTarget)
    {
        const string definitionKey = " owner:上海/config?revision=1#opaque%25 ";
        var path = LogicalPath.FromProperties("flight.上海", "revision[2]?x=%#");

        var requests = ConfigurationHistoryPage.CreateRequests(definitionKey, path.ToCanonicalString(), target, null, null, TimeZoneInfo.Utc);

        Assert.Equal(definitionKey, requests.History.DefinitionKey);
        Assert.Equal(definitionKey, requests.Groups.DefinitionKey);
        Assert.Equal(path, requests.History.LogicalPath);
        Assert.Equal(expectedTarget, requests.History.TargetKind);
        Assert.Equal(50, requests.History.PageSize);
        Assert.Equal(50, requests.Groups.PageSize);
        Assert.Null(requests.History.Cursor);
        Assert.Null(requests.Groups.Cursor);
        requests.History.Validate();
        requests.Groups.Validate();
    }

    [Fact]
    public void CreateRequests_WhenDatesAreSelected_ShouldShareInclusiveCalendarBoundsAcrossBothQueries()
    {
        var from = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

        var requests = ConfigurationHistoryPage.CreateRequests("definition", "", "all", from, to, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(from), requests.History.From);
        Assert.Equal(new DateTimeOffset(to.AddDays(1).AddTicks(-1)), requests.History.To);
        Assert.Equal(requests.History.From, requests.Groups.From);
        Assert.Equal(requests.History.To, requests.Groups.To);
        requests.History.Validate();
        requests.Groups.Validate();
    }

    [Fact]
    public void CreateRequests_WhenFiltersAreEmpty_ShouldRequestUnfilteredFirstPages()
    {
        var requests = ConfigurationHistoryPage.CreateRequests(null, "  ", "all", null, null, TimeZoneInfo.Utc);

        Assert.Null(requests.History.DefinitionKey);
        Assert.Null(requests.History.LogicalPath);
        Assert.Null(requests.History.TargetKind);
        Assert.Null(requests.History.From);
        Assert.Null(requests.History.To);
        Assert.Null(requests.Groups.DefinitionKey);
        Assert.Null(requests.Groups.From);
        Assert.Null(requests.Groups.To);
    }

    [Theory]
    [InlineData("flight[$unterminated")]
    [InlineData("flight[@-1]")]
    public void CreateRequests_WhenLogicalPathIsInvalid_ShouldRejectInsteadOfBroadeningTheQuery(string path)
    {
        Assert.Throws<ConfigurationPathFormatException>(() =>
            ConfigurationHistoryPage.CreateRequests("definition", path, "all", null, null, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public void CreateRequests_WhenClockTimezoneIsExplicit_ShouldInterpretPickerDatesAsDeploymentCalendarDates(DateTimeKind kind)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("calendar-review", TimeSpan.FromMinutes(345), "calendar-review", "calendar-review");
        var from = new DateTime(2026, 10, 10, 15, 30, 0, kind);
        var to = new DateTime(2026, 10, 11, 9, 0, 0, kind);

        var requests = ConfigurationHistoryPage.CreateRequests("definition", "", "all", from, to, zone);

        Assert.Equal(new DateTimeOffset(2026, 10, 9, 18, 15, 0, TimeSpan.Zero), requests.History.From);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 18, 15, 0, TimeSpan.Zero).AddTicks(-1), requests.History.To);
        Assert.Equal(TimeSpan.FromMinutes(345), requests.History.From!.Value.Offset);
        Assert.Equal(requests.History.From, requests.Groups.From);
        Assert.Equal(requests.History.To, requests.Groups.To);
        requests.History.Validate();
        requests.Groups.Validate();
    }

    [Fact]
    public void CreateRequests_WhenDayContainsOffsetTransition_ShouldUseEachBoundaryOffset()
    {
        var zone = DaylightZone(startHour: 2, endHour: 2);
        var date = new DateTime(2026, 3, 10);

        var requests = ConfigurationHistoryPage.CreateRequests(null, "", "all", date, date, zone);
        var start = requests.History.From!.Value;
        var end = requests.History.To!.Value;

        Assert.Equal(TimeSpan.Zero, start.Offset);
        Assert.Equal(TimeSpan.FromHours(1), end.Offset);
        Assert.Equal(TimeSpan.FromHours(23).Add(TimeSpan.FromTicks(-1)), end - start);
    }

    [Theory]
    [InlineData(3, 10, "MissingBoundary")]
    [InlineData(11, 3, "RepeatedBoundary")]
    public void CreateRequests_WhenDayBoundaryIsNotUnique_ShouldRejectInsteadOfGuessingAnInstant(
        int month, int day, string error)
    {
        var zone = DaylightZone(startHour: 0, endHour: 1);
        var date = new DateTime(2026, month, day);

        var exception = Assert.Throws<ConfigurationCalendarRangeException>(() =>
            ConfigurationHistoryPage.CreateRequests(null, "", "all", date, null, zone));

        Assert.Equal(error, exception.Error.ToString());
    }

    [Fact]
    public void CreateRequests_WhenCalendarRangeIsReversed_ShouldRejectBeforeReadingHistory()
    {
        var exception = Assert.Throws<ConfigurationCalendarRangeException>(() =>
            ConfigurationHistoryPage.CreateRequests(null, "", "all", new DateTime(2026, 10, 11), new DateTime(2026, 10, 10), TimeZoneInfo.Utc));

        Assert.Equal(ConfigurationCalendarRangeError.Reversed, exception.Error);
    }

    private static TimeZoneInfo DaylightZone(int startHour, int endHour)
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, startHour, 0, 0), 3, 10);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, endHour, 0, 0), 11, 3);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("calendar-dst-review", TimeSpan.Zero, "calendar-dst-review", "standard", "daylight", [rule]);
    }
}
