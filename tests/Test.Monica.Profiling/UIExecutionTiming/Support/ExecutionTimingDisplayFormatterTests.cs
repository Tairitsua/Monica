using System.Globalization;
using AwesomeAssertions;
using Monica.Profiling.ExecutionTiming.Models;
using Monica.Profiling.UIExecutionTiming.Support;
using Xunit;

namespace Test.Monica.Profiling.UIExecutionTiming.Support;

public sealed class ExecutionTimingDisplayFormatterTests
{
    [Fact]
    public void MachineTime_WhenTimestampExists_ShouldKeepTheOriginalInstant()
    {
        var timestamp = new DateTimeOffset(2026, 10, 8, 9, 30, 15, TimeSpan.FromHours(-4));

        var machineTime = ExecutionTimingDisplayFormatter.FormatMachineDateTime(timestamp);
        DateTimeOffset.ParseExact(machineTime, "O", CultureInfo.InvariantCulture).Should().Be(timestamp);
        machineTime.Should().Be("2026-10-08T09:30:15.0000000-04:00");
    }

    [Fact]
    public void MachineTime_WhenUnavailable_ShouldKeepMissingEvidenceDistinctFromAnInstant()
    {
        ExecutionTimingDisplayFormatter.FormatMachineDateTime(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]
    public void FormatRatePerMinute_WhenLessThanOneMinuteHasElapsed_ShouldUseTheDocumentedDenominatorFloor(int elapsedSeconds)
    {
        var first = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        var statistics = new ExecutionTimingStatistics("formatter.rate", "Rate", 6, 10, first);

        ExecutionTimingDisplayFormatter.FormatRatePerMinute(statistics, first.AddSeconds(elapsedSeconds))
            .Should().Be(6d.ToString("0.##", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void FormatRatePerMinute_WhenCaptureIsRetained_ShouldUseFixedCaptureTimeInsteadOfRenderTime()
    {
        var first = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        var statistics = new ExecutionTimingStatistics("formatter.rate", "Rate", 6, 10, first);
        var capturedAt = first.AddMinutes(4);

        ExecutionTimingDisplayFormatter.FormatRatePerMinute(statistics, capturedAt)
            .Should().Be(1.5d.ToString("0.##", CultureInfo.CurrentCulture));
        ExecutionTimingDisplayFormatter.FormatRatePerMinute(statistics, first.AddMinutes(6))
            .Should().Be(1d.ToString("0.##", CultureInfo.CurrentCulture));
        ExecutionTimingDisplayFormatter.FormatRatePerMinute(statistics, capturedAt)
            .Should().Be(1.5d.ToString("0.##", CultureInfo.CurrentCulture));
    }

    [Fact]
    public void FormatRunningDuration_WhenCapturePrecedesStart_ShouldClampNegativeAgeToZero()
    {
        var startedAt = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        var running = new RunningExecutionTimingInfo(Guid.Empty, "formatter.running", "Running", startedAt, null);

        ExecutionTimingDisplayFormatter.FormatRunningDuration(running, startedAt.AddSeconds(-1)).Should().Be("0s");
        ExecutionTimingDisplayFormatter.FormatRunningDuration(running, startedAt.AddSeconds(2.5))
            .Should().Be($"{2.5d:0.##}s");
    }

    [Fact]
    public void FormatMemory_WhenZeroAndMissingAreDistinct_ShouldPreserveThatDifference()
    {
        ExecutionTimingDisplayFormatter.FormatMemory(null).Should().Be("—");
        ExecutionTimingDisplayFormatter.FormatMemory(0).Should().Be("0B");
        ExecutionTimingDisplayFormatter.FormatMemory(1024).Should().Be("1KB");
    }
}
