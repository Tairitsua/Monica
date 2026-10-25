using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Core.Clock;
using Monica.Modules;
using Monica.UI.Shared.Services;
using Xunit;

namespace Test.Monica.UI.Shared.Services;

public sealed class MoTimeFormatterTests
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 16, 15, 4, 5, 729, TimeSpan.Zero);

    [Fact]
    public void Format_WhenClockIsUnset_ShouldUseUtcWithoutOffsetOrFractions()
    {
        Create().Format(Instant.ToOffset(TimeSpan.FromHours(-7))).Should().Be("2026-10-16 15:04:05");
    }

    [Fact]
    public void Format_WhenClockIsConfigured_ShouldConvertDateAcrossMidnight()
    {
        var formatter = Create(CommonTimeZone.Japan);
        formatter.Format(Instant).Should().Be("2026-10-17 00:04:05");
        formatter.FormatDate(Instant).Should().Be("2026-10-17");
        formatter.FormatTime(Instant).Should().Be("00:04:05");
    }

    [Fact]
    public void Format_WhenUtcOrLocalDateTime_ShouldRenderTheSameInstant()
    {
        var formatter = Create(CommonTimeZone.China);
        formatter.Format(Instant.UtcDateTime).Should().Be("2026-10-16 23:04:05");
        formatter.Format(Instant.LocalDateTime).Should().Be("2026-10-16 23:04:05");
    }

    [Fact]
    public void Format_WhenUnspecifiedAuditWallTime_ShouldPreserveItsValue()
    {
        var wallTime = new DateTime(2026, 10, 16, 15, 4, 5, DateTimeKind.Unspecified);
        Create(CommonTimeZone.China).Format(wallTime).Should().Be("2026-10-16 15:04:05");
        Create(CommonTimeZone.China).Format(DateTime.MinValue).Should().Be("0001-01-01 00:00:00");
    }

    [Fact]
    public void Format_WhenOptionsOverrideFormats_ShouldApplyToAllDisplayShapes()
    {
        var shell = new ModuleShellUIOption();
        shell.TimeDisplay.DateTimeFormat = "yyyy/MM/dd HH:mm";
        shell.TimeDisplay.DateFormat = "yyyy/MM/dd";
        shell.TimeDisplay.TimeFormat = "HH:mm";
        var formatter = Create(CommonTimeZone.China, shell);
        formatter.Format(Instant).Should().Be("2026/10/16 23:04");
        formatter.FormatRange(Instant, Instant.AddMinutes(1)).Should().Be("2026/10/16 23:04 → 23:05");
        formatter.FormatDate(Instant).Should().Be("2026/10/16");
        formatter.FormatTime(Instant.UtcDateTime).Should().Be("23:04");
        formatter.Format(Instant, "HH:mm:ss.fff").Should().Be("23:04:05.729");
    }

    [Fact]
    public void Format_WhenOffsetIsExplicitlyConfigured_ShouldKeepTheClockOffsetForBothTypes()
    {
        var formatter = Create(CommonTimeZone.China);
        formatter.Format(Instant, "yyyy-MM-dd HH:mm:ss zzz").Should().Be("2026-10-16 23:04:05 +08:00");
        formatter.Format(Instant.UtcDateTime, "yyyy-MM-dd HH:mm:ss zzz").Should().Be("2026-10-16 23:04:05 +08:00");
        formatter.Format(new DateTime(2026, 10, 16, 23, 4, 5), "yyyy-MM-dd HH:mm:ss zzz")
            .Should().Be("2026-10-16 23:04:05 +08:00");
    }

    [Fact]
    public void FormatRange_WhenCrossingDeploymentMidnight_ShouldIncludeBothDates()
    {
        Create(CommonTimeZone.China).FormatRange(Instant, Instant.AddHours(2))
            .Should().Be("2026-10-16 23:04:05 → 2026-10-17 01:04:05");
    }

    [Fact]
    public void FormatRange_WhenDaylightSavingRepeatsAnHour_ShouldDistinguishTheActualEndpoints()
    {
        var start = new DateTimeOffset(2026, 11, 1, 5, 50, 0, TimeSpan.Zero);
        var end = start.AddMinutes(20);
        Create(CommonTimeZone.USEastern).FormatRange(start, end)
            .Should().Be("2026-11-01 01:50:00 -04:00 → 01:10:00 -05:00");
    }

    [Fact]
    public void FormatWallTime_WhenScheduleZoneDiffersFromClock_ShouldRetainItsDeclaredOffset()
    {
        var shell = new ModuleShellUIOption();
        shell.TimeDisplay.DateTimeFormat = "yyyy-MM-dd HH:mm:ss zzz";
        Create(CommonTimeZone.China, shell).FormatWallTime(Instant.ToOffset(TimeSpan.FromHours(9)))
            .Should().Be("2026-10-17 00:04:05 +09:00");
    }

    [Fact]
    public void ToDisplayTime_WhenZoneUsesDaylightSaving_ShouldUseTheInstantSpecificOffset()
    {
        var formatter = Create(CommonTimeZone.USEastern);
        formatter.ToDisplayTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)).Offset
            .Should().Be(TimeSpan.FromHours(-5));
        formatter.ToDisplayTime(new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero)).Offset
            .Should().Be(TimeSpan.FromHours(-4));
    }

    [Fact]
    public void Format_WhenMultipleHostsHaveDifferentClockPolicies_ShouldKeepThemIsolated()
    {
        var china = Create(CommonTimeZone.China);
        var japan = Create(CommonTimeZone.Japan);
        china.Format(Instant).Should().Be("2026-10-16 23:04:05");
        japan.Format(Instant).Should().Be("2026-10-17 00:04:05");
        china.Format(Instant).Should().Be("2026-10-16 23:04:05");
    }

    [Fact]
    public void Format_WhenTimestampIsMissing_ShouldUseTheSharedEmptyValue()
    {
        var formatter = Create();
        formatter.Format((DateTime?)null).Should().Be("—");
        formatter.Format((DateTimeOffset?)null).Should().Be("—");
    }

    private static MoTimeFormatter Create(CommonTimeZone? zone = null, ModuleShellUIOption? shell = null) =>
        new(Options.Create(shell ?? new ModuleShellUIOption()),
            Options.Create(new ModuleClockOption { LocalTimeZone = zone }));
}
