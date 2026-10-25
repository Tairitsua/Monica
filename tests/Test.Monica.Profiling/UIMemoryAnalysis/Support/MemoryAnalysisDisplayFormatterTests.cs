using System.Globalization;
using AwesomeAssertions;
using Monica.Profiling.UIMemoryAnalysis.Support;
using Xunit;

namespace Test.Monica.Profiling.UIMemoryAnalysis.Support;

public sealed class MemoryAnalysisDisplayFormatterTests
{
    [Fact]
    public void ReportedBytes_ShouldPreserveFractionalCountersAndRejectNonFiniteMeasurements()
    {
        MemoryAnalysisDisplayFormatter.ReportedBytes(1536.5).Should().Be($"{(1536.5d / 1024d):0.##} KiB");
        MemoryAnalysisDisplayFormatter.ReportedBytes(0.5).Should().Be($"{0.5d:0.##} B");
        MemoryAnalysisDisplayFormatter.ReportedBytes(double.NaN).Should().Be("—");
        MemoryAnalysisDisplayFormatter.ReportedBytes(double.PositiveInfinity).Should().Be("—");
    }

    [Fact]
    public void Format_WhenEvidenceIsMissing_ShouldKeepItDistinctFromRecordedZero()
    {
        MemoryAnalysisDisplayFormatter.Bytes(null).Should().Be("—");
        MemoryAnalysisDisplayFormatter.Count(null).Should().Be("—");
        MemoryAnalysisDisplayFormatter.Percent(null).Should().Be("—");
        MemoryAnalysisDisplayFormatter.Milliseconds(null).Should().Be("—");

        MemoryAnalysisDisplayFormatter.Bytes(0).Should().Be("0 B");
        MemoryAnalysisDisplayFormatter.SignedBytes(0).Should().Be("0 B");
        MemoryAnalysisDisplayFormatter.Count(0).Should().Be("0");
        MemoryAnalysisDisplayFormatter.Percent(0).Should().Be($"{0d:0.0}%");
        MemoryAnalysisDisplayFormatter.Milliseconds(TimeSpan.Zero).Should().Be($"{0d:0.00} ms");
    }

    [Theory]
    [InlineData(1023L, 1023d, "B")]
    [InlineData(1024L, 1d, "KiB")]
    [InlineData(1536L, 1.5d, "KiB")]
    [InlineData(1_048_576L, 1d, "MiB")]
    [InlineData(1_073_741_824L, 1d, "GiB")]
    [InlineData(1_099_511_627_776L, 1d, "TiB")]
    [InlineData(-1536L, -1.5d, "KiB")]
    public void Bytes_WhenCrossingBinaryUnitBoundaries_ShouldUseBinaryNamesAndPreserveDirection(
        long bytes, double scaledValue, string unit)
    {
        MemoryAnalysisDisplayFormatter.Bytes(bytes)
            .Should().Be($"{scaledValue.ToString("0.##", CultureInfo.CurrentCulture)} {unit}");
    }

    [Fact]
    public void SignedBytes_WhenMovementDecreasesOrIncreases_ShouldShowTheActualSignAndHandleIntegerLimits()
    {
        MemoryAnalysisDisplayFormatter.SignedBytes(1536).Should().Be($"+{1.5d:0.##} KiB");
        MemoryAnalysisDisplayFormatter.SignedBytes(-1536).Should().Be($"-{1.5d:0.##} KiB");
        MemoryAnalysisDisplayFormatter.SignedBytes(long.MinValue).Should().Be("-8 EiB");
        MemoryAnalysisDisplayFormatter.Bytes(long.MinValue).Should().Be("-8 EiB");
        MemoryAnalysisDisplayFormatter.SignedBytes(long.MaxValue).Should().Be("+8 EiB");
        MemoryAnalysisDisplayFormatter.Raw(long.MaxValue).Should().Be("9223372036854775807");
    }

    [Fact]
    public void RatioPercent_WhenDenominatorIsUnavailable_ShouldAvoidAFalseZeroOrClamping()
    {
        MemoryAnalysisDisplayFormatter.RatioPercent(0, 0).Should().Be("—");
        MemoryAnalysisDisplayFormatter.RatioPercent(100, -1).Should().Be("—");
        MemoryAnalysisDisplayFormatter.RatioPercent(0, 100).Should().Be($"{0d:0.0}%");
        MemoryAnalysisDisplayFormatter.RatioPercent(150, 100).Should().Be($"{150d:0.0}%");
        MemoryAnalysisDisplayFormatter.RatioPercent(long.MaxValue, long.MaxValue).Should().Be($"{100d:0.0}%");
    }

    [Theory]
    [InlineData("en-US", "1.5 KiB", "+1.5 KiB", "1.3%", "1.23 ms")]
    [InlineData("fr-FR", "1,5 KiB", "+1,5 KiB", "1,3%", "1,23 ms")]
    public void Format_WhenDisplayCultureChanges_ShouldLocalizeNumbersWithoutChangingMeasurementUnits(
        string cultureName, string bytes, string movement, string percentage, string duration)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            MemoryAnalysisDisplayFormatter.Bytes(1536).Should().Be(bytes);
            MemoryAnalysisDisplayFormatter.SignedBytes(1536).Should().Be(movement);
            MemoryAnalysisDisplayFormatter.Percent(1.26).Should().Be(percentage);
            MemoryAnalysisDisplayFormatter.Milliseconds(TimeSpan.FromTicks(12_345)).Should().Be(duration);
            MemoryAnalysisDisplayFormatter.Count(1234).Should().Be($"1{CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator}234");
            MemoryAnalysisDisplayFormatter.Raw(1234).Should().Be("1234");
            const double recorded = 1_234_567.8901234567;
            var raw = MemoryAnalysisDisplayFormatter.Raw(recorded);
            raw.Should().Be("1234567.8901234567");
            double.Parse(raw, CultureInfo.InvariantCulture).Should().Be(recorded);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void MachineTime_WhenSnapshotIsUtcUnspecifiedOrLocal_ShouldPreserveTheExactUtcIdentity()
    {
        var utc = new DateTime(2026, 10, 9, 3, 4, 5, 678, DateTimeKind.Utc).AddTicks(1234);
        var local = utc.ToLocalTime();
        foreach (var timestamp in new[] { utc, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), local })
        {
            var raw = MemoryAnalysisDisplayFormatter.MachineTime(timestamp);
            raw.Should().Be("2026-10-09T03:04:05.6781234Z");
            DateTime.ParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Should().Be(utc);
        }
    }

}
