namespace Monica.UI.Shared.Services;

/// <summary>
/// Host-wide formats for human-readable timestamps in Monica pages. Conversion uses
/// <see cref="Monica.Modules.ModuleClockOption.LocalTimeZone"/>; these options do not change stored values.
/// </summary>
public sealed class MoTimeDisplayOptions
{
    /// <summary>
    /// Gets or sets the .NET format used for a full timestamp. Defaults to
    /// <c>yyyy-MM-dd HH:mm:ss</c>, without fractional seconds or a repeated UTC offset.
    /// </summary>
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Gets or sets the date format. Defaults to <c>yyyy-MM-dd</c>.</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>Gets or sets the time-only format. Defaults to <c>HH:mm:ss</c>.</summary>
    public string TimeFormat { get; set; } = "HH:mm:ss";
}
