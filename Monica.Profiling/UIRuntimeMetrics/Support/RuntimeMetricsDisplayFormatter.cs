using System.Globalization;

namespace Monica.Profiling.UIRuntimeMetrics.Support;

/// <summary>
/// Formats runtime observations without changing their measurement or collection semantics.
/// </summary>
internal static class RuntimeMetricsDisplayFormatter
{
    private const string UNAVAILABLE = "—";
    private const double BYTES_PER_KIBIBYTE = 1024d;
    private const double BYTES_PER_MEBIBYTE = BYTES_PER_KIBIBYTE * BYTES_PER_KIBIBYTE;

    /// <summary>
    /// Formats a measured percentage, or an unavailable marker when no observation exists.
    /// </summary>
    public static string Percent(double? value) =>
        value.HasValue ? $"{value.Value.ToString("0.0", CultureInfo.CurrentCulture)}%" : UNAVAILABLE;

    /// <summary>
    /// Formats a value already expressed in decimal megabytes, without applying a byte conversion.
    /// </summary>
    public static string Megabytes(double? value) =>
        value.HasValue ? $"{value.Value.ToString("0.0", CultureInfo.CurrentCulture)} MB" : UNAVAILABLE;

    /// <summary>
    /// Formats a signed change already expressed in decimal megabytes; zero carries no direction.
    /// </summary>
    public static string SignedMegabytes(double value) =>
        $"{value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)} MB";

    /// <summary>
    /// Formats the collector-reported byte rate using binary KiB/s or MiB/s units when appropriate.
    /// The value is not normalized by either the collector interval or the view refresh interval.
    /// </summary>
    public static string ReportedAllocationRate(double? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue)
        {
            return UNAVAILABLE;
        }

        var value = bytesPerSecond.Value;
        var absoluteValue = Math.Abs(value);
        var (scaledValue, unit) = absoluteValue switch
        {
            >= BYTES_PER_MEBIBYTE => (value / BYTES_PER_MEBIBYTE, "MiB/s"),
            >= BYTES_PER_KIBIBYTE => (value / BYTES_PER_KIBIBYTE, "KiB/s"),
            _ => (value, "B/s")
        };

        return $"{scaledValue.ToString("0.##", CultureInfo.CurrentCulture)} {unit}";
    }

    /// <summary>
    /// Formats the exact sample timestamp as round-trip UTC text.
    /// Unspecified sample timestamps are interpreted as UTC, matching the collector contract.
    /// </summary>
    public static string MachineTime(DateTime timestamp) =>
        AsUtc(timestamp).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Preserves general numeric precision in the current culture and appends the metric's unit.</summary>
    public static string PlotValue(double value, RuntimeTrendMetric metric) =>
        $"{value.ToString("G", CultureInfo.CurrentCulture)} {RuntimeTrendPlotProjection.UnitFor(metric)}";

    /// <summary>
    /// Formats a complete finite tick domain using one culture-aware notation and precision.
    /// Ordinary axes use decimals; magnitudes below 0.000001 or at least 1,000,000 use scientific notation.
    /// Precision retains three significant digits of the smallest interval without changing the tick values.
    /// </summary>
    public static IReadOnlyList<string> AxisLabels(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return [];
        var magnitude = values.Max(value => Math.Abs(value));
        if (magnitude == 0) return values.Select(_ => "0").ToArray();

        // Scale before subtracting so an extreme signed domain cannot overflow its interval.
        var scaledValues = values.Select(value => value / magnitude).Distinct().Order().ToArray();
        var spacing = scaledValues.Zip(scaledValues.Skip(1), (left, right) => right - left)
            .DefaultIfEmpty(1).Min();
        var scientific = magnitude is < .000001 or >= 1_000_000;
        var precision = scientific
            ? Math.Min(16, Math.Max(0, 2 - (int)Math.Floor(Math.Log10(spacing))))
            : Math.Max(0, 2 - (int)Math.Floor(Math.Log10(magnitude) + Math.Log10(spacing)));

        string[] FormatLabels()
        {
            var format = (precision == 0 ? "0" : "0." + new string('0', precision))
                + (scientific ? "E+0" : string.Empty);
            return values.Select(value => value == 0 ? "0" : value.ToString(format, CultureInfo.CurrentCulture)).ToArray();
        }

        var labels = FormatLabels();
        // Remove only precision that is redundant for the whole ordinary axis, never for one tick alone.
        while (!scientific && precision > 0 && labels.Where((_, index) => values[index] != 0).All(label => label.EndsWith('0')))
        {
            precision--;
            labels = FormatLabels();
        }
        return labels;
    }

    /// <summary>
    /// Formats an unscaled measurement as culture-independent, round-trip numeric evidence.
    /// </summary>
    public static string Raw(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Normalizes an instant to UTC; unspecified collector timestamps are already UTC.</summary>
    public static DateTime AsUtc(DateTime timestamp) =>
        timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            : timestamp.ToUniversalTime();

    /// <summary>Normalizes an optional collector instant without inventing a missing observation.</summary>
    public static DateTime? AsUtc(DateTime? timestamp) => timestamp is { } value ? AsUtc(value) : null;
}
