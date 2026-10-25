using System.Globalization;
using AwesomeAssertions;
using Monica.Profiling.UIRuntimeMetrics.Support;
using Xunit;

namespace Test.Monica.Profiling.UIRuntimeMetrics.Support;

public sealed class RuntimeMetricsDisplayFormatterTests
{
    [Fact]
    public void Format_WhenObservationIsMissing_ShouldDistinguishItFromMeasuredZero()
    {
        RuntimeMetricsDisplayFormatter.Percent(null).Should().Be("—");
        RuntimeMetricsDisplayFormatter.Megabytes(null).Should().Be("—");
        RuntimeMetricsDisplayFormatter.ReportedAllocationRate(null).Should().Be("—");

        RuntimeMetricsDisplayFormatter.Percent(0).Should().Be($"{0d:0.0}%");
        RuntimeMetricsDisplayFormatter.Megabytes(0).Should().Be($"{0d:0.0} MB");
        RuntimeMetricsDisplayFormatter.ReportedAllocationRate(0).Should().Be("0 B/s");
        RuntimeMetricsDisplayFormatter.SignedMegabytes(0).Should().Be($"{0d:0.0} MB");
    }

    [Theory]
    [InlineData(1023d, 1023d, "B/s")]
    [InlineData(1024d, 1d, "KiB/s")]
    [InlineData(1536d, 1.5d, "KiB/s")]
    [InlineData(1_048_576d, 1d, "MiB/s")]
    [InlineData(1_572_864d, 1.5d, "MiB/s")]
    [InlineData(-1024d, -1d, "KiB/s")]
    [InlineData(-1_572_864d, -1.5d, "MiB/s")]
    public void ReportedAllocationRate_WhenCrossingBinaryThresholds_ShouldScaleWithoutChangingTheReportedRate(
        double reportedBytesPerSecond, double scaledValue, string expectedUnit)
    {
        RuntimeMetricsDisplayFormatter.ReportedAllocationRate(reportedBytesPerSecond)
            .Should().Be($"{scaledValue.ToString("0.##", CultureInfo.CurrentCulture)} {expectedUnit}");
    }

    [Theory]
    [InlineData("en-US", "1.3%", "1.3 MB", "+1.3 MB", "-1.3 MB", "1.5 KiB/s")]
    [InlineData("fr-FR", "1,3%", "1,3 MB", "+1,3 MB", "-1,3 MB", "1,5 KiB/s")]
    public void Format_WhenCultureChanges_ShouldLocalizeDisplayAndKeepAlreadyDecimalMegabytesUnscaled(
        string cultureName, string percent, string megabytes, string increase, string decrease, string allocation)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            RuntimeMetricsDisplayFormatter.Percent(1.26).Should().Be(percent);
            RuntimeMetricsDisplayFormatter.Megabytes(1.26).Should().Be(megabytes);
            RuntimeMetricsDisplayFormatter.SignedMegabytes(1.26).Should().Be(increase);
            RuntimeMetricsDisplayFormatter.SignedMegabytes(-1.26).Should().Be(decrease);
            RuntimeMetricsDisplayFormatter.ReportedAllocationRate(1536).Should().Be(allocation);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void Raw_WhenDisplayCultureUsesComma_ShouldPreserveUnscaledRoundTripEvidence()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            const double rawMeasurement = 1_234_567.8901234567;

            var evidence = RuntimeMetricsDisplayFormatter.Raw(rawMeasurement);

            evidence.Should().Be("1234567.8901234567");
            double.Parse(evidence, CultureInfo.InvariantCulture).Should().Be(rawMeasurement);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void MachineTime_WhenSampleIsUtcOrUnspecified_ShouldRetainTheExactInstant()
    {
        var utc = new DateTime(2026, 10, 9, 3, 4, 5, 678, DateTimeKind.Utc).AddTicks(1234);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        var local = utc.ToLocalTime();

        foreach (var timestamp in new[] { utc, unspecified, local })
        {
            var evidence = RuntimeMetricsDisplayFormatter.MachineTime(timestamp);
            evidence.Should().Be("2026-10-09T03:04:05.6781234Z");
            DateTime.ParseExact(evidence, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .Should().Be(utc);
        }
    }

    [Theory]
    [InlineData("en-US", "345.75123456789 %", "3.75000025 MB")]
    [InlineData("fr-FR", "345,75123456789 %", "3,75000025 MB")]
    public void FormatPlot_WhenCultureChanges_ShouldLocalizeNumbersWithoutRoundingTheReadoutOrRescalingMegabytes(
        string cultureName, string percent, string megabytes)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            RuntimeMetricsDisplayFormatter.PlotValue(345.75123456789, RuntimeTrendMetric.Cpu).Should().Be(percent);
            RuntimeMetricsDisplayFormatter.PlotValue(3.75000025, RuntimeTrendMetric.WorkingSetMegabytes).Should().Be(megabytes);
            RuntimeMetricsDisplayFormatter.Raw(345.75123456789).Should().Be("345.75123456789");
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Theory]
    [InlineData("en-US", "0.1936", "0.1452", "0.0968", "0.0484")]
    [InlineData("fr-FR", "0,1936", "0,1452", "0,0968", "0,0484")]
    public void AxisLabels_WhenCpuTicksCrossTheFormerPerValueThreshold_ShouldUseOneCultureAwareDecimalPrecision(
        string cultureName, string maximum, string upper, string middle, string lower)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var values = new[] { .1936, .1452, .0968, .0484, 0d };

            var labels = RuntimeMetricsDisplayFormatter.AxisLabels(values);

            labels.Should().Equal(maximum, upper, middle, lower, "0");
            labels.Select(label => double.Parse(label, CultureInfo.CurrentCulture)).Should().Equal(values);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Theory]
    [InlineData(.0492, "0.0492", "0.0369", "0.0246", "0.0123")]
    [InlineData(.0000492, "0.0000492", "0.0000369", "0.0000246", "0.0000123")]
    [InlineData(.3, "0.300", "0.225", "0.150", "0.075")]
    public void AxisLabels_WhenOrdinaryCpuTicksNeedMoreThanOneDecimal_ShouldKeepEveryNeighborDistinctWithoutExponents(
        double maximum, string top, string upper, string middle, string lower)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var values = new[] { maximum, maximum * .75, maximum * .5, maximum * .25, 0 };

            var labels = RuntimeMetricsDisplayFormatter.AxisLabels(values);

            labels.Should().Equal(top, upper, middle, lower, "0");
            labels.Distinct().Should().HaveCount(values.Length);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Theory]
    [InlineData("en-US", "1.250E+6", "9.375E+5", "6.250E+5", "3.125E+5")]
    [InlineData("fr-FR", "1,250E+6", "9,375E+5", "6,250E+5", "3,125E+5")]
    public void AxisLabels_WhenTheDomainIsLarge_ShouldUseScientificNotationEvenForItsSmallerTicks(
        string cultureName, string maximum, string upper, string middle, string lower)
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var values = new[] { 1_250_000d, 937_500, 625_000, 312_500, 0 };

            var labels = RuntimeMetricsDisplayFormatter.AxisLabels(values);

            labels.Should().Equal(maximum, upper, middle, lower, "0");
            labels.Select(label => double.Parse(label, CultureInfo.CurrentCulture)).Should().Equal(values);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void AxisLabels_WhenTheWholeDomainIsExtremelySmall_ShouldUseScientificNotationConsistently()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            RuntimeMetricsDisplayFormatter.AxisLabels([4.92e-8, 3.69e-8, 2.46e-8, 1.23e-8, 0])
                .Should().Equal("4.920E-8", "3.690E-8", "2.460E-8", "1.230E-8", "0");
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void AxisLabels_WhenFiniteSignedValuesAreExtreme_ShouldKeepDistinctLabelsWithoutOverflowingTheSpacing(
        double magnitude)
    {
        var labels = RuntimeMetricsDisplayFormatter.AxisLabels([magnitude, 0, -magnitude]);

        labels.Should().HaveCount(3);
        labels.Distinct().Should().HaveCount(3);
        labels[0].Should().Contain("E").And.NotContain("Infinity").And.NotContain("NaN");
        labels[1].Should().Be("0");
        labels[2].Should().StartWith(CultureInfo.CurrentCulture.NumberFormat.NegativeSign).And.Contain("E");
    }

    [Fact]
    public void AxisLabels_WhenTicksAreEmptyOrAllZero_ShouldAvoidInventingMagnitudeOrSpacing()
    {
        RuntimeMetricsDisplayFormatter.AxisLabels([]).Should().BeEmpty();
        RuntimeMetricsDisplayFormatter.AxisLabels([0, 0, 0]).Should().Equal("0", "0", "0");
    }

    [Fact]
    public void MachineTime_WhenCalendarAndTimestampKindDiffer_ShouldKeepInvariantExactEvidence()
    {
        var priorCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("th-TH");
            var utc = new DateTime(2026, 10, 15, 3, 4, 5, 678, DateTimeKind.Utc).AddTicks(1234);
            var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

            foreach (var timestamp in new[] { utc, unspecified, utc.ToLocalTime() })
            {
                RuntimeMetricsDisplayFormatter.MachineTime(timestamp).Should().Be("2026-10-15T03:04:05.6781234Z");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

}
