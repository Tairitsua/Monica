using System.Globalization;

namespace Monica.Profiling.UIMemoryAnalysis.Support;

/// <summary>
/// Formats memory evidence without conflating its measurement bases or changing recorded values.
/// </summary>
internal static class MemoryAnalysisDisplayFormatter
{
    private const string UNAVAILABLE = "—";
    private static readonly string[] BYTE_UNITS = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    /// <summary>
    /// Formats bytes with explicit binary units, or an unavailable marker for missing evidence.
    /// </summary>
    public static string Bytes(long? value) => value.HasValue ? FormatBytes(value.Value, signed: false) : UNAVAILABLE;

    /// <summary>Formats a collector-reported byte counter without converting it to an integer.</summary>
    public static string ReportedBytes(double value)
    {
        if (!double.IsFinite(value)) return UNAVAILABLE;
        var unit = 0;
        while (Math.Abs(value) >= 1024d && unit < BYTE_UNITS.Length - 1)
        {
            value /= 1024d;
            unit++;
        }
        return $"{value.ToString("0.##", CultureInfo.CurrentCulture)} {BYTE_UNITS[unit]}";
    }

    /// <summary>
    /// Formats signed before-minus-after byte movement. Positive values are net decreases; zero has no sign.
    /// This value is not an exact count of freed objects, and negative movement is not a failure.
    /// </summary>
    public static string SignedBytes(long value) => FormatBytes(value, signed: true);

    /// <summary>Formats a recorded integer count without replacing missing evidence with zero.</summary>
    public static string Count(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? UNAVAILABLE;

    /// <summary>Formats a recorded percentage without applying a severity threshold or a bound.</summary>
    public static string Percent(double? value) =>
        value.HasValue ? $"{value.Value.ToString("0.0", CultureInfo.CurrentCulture)}%" : UNAVAILABLE;

    /// <summary>
    /// Formats numerator divided by the reported denominator as a percentage. A nonpositive denominator
    /// is unavailable; a reported ratio is neither clamped to 100 percent nor treated as a health decision.
    /// </summary>
    public static string RatioPercent(long numerator, long denominator) =>
        denominator > 0 ? Percent((double)numerator / denominator * 100d) : UNAVAILABLE;

    /// <summary>Formats the exposed pause duration in milliseconds without calling it total collection time.</summary>
    public static string Milliseconds(TimeSpan? value) =>
        value.HasValue ? $"{value.Value.TotalMilliseconds.ToString("0.00", CultureInfo.CurrentCulture)} ms" : UNAVAILABLE;

    /// <summary>Formats the exact snapshot timestamp as round-trip UTC evidence.</summary>
    public static string MachineTime(DateTime timestamp) => AsUtc(timestamp).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Formats an unscaled integer as culture-independent exact evidence.</summary>
    public static string Raw(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats an unscaled floating-point measurement as culture-independent round-trip evidence.</summary>
    public static string Raw(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string FormatBytes(decimal value, bool signed)
    {
        var unit = 0;
        while (Math.Abs(value) >= 1024m && unit < BYTE_UNITS.Length - 1)
        {
            value /= 1024m;
            unit++;
        }

        var format = signed ? "+0.##;-0.##;0" : "0.##";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {BYTE_UNITS[unit]}";
    }

    private static DateTime AsUtc(DateTime timestamp) => timestamp.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        : timestamp.ToUniversalTime();
}
