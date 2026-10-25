using System.Text.Json;
using Microsoft.Extensions.Logging;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.Tool.Extensions;
using MudBlazor;

namespace Monica.Framework.UI.UIObservableInstance.Models;

/// <summary>
/// UI-friendly view model for ObservableInstanceTracker instances
/// </summary>
public class ObservableInstanceViewModel
{
    #region Identity

    /// <summary>
    /// Unique instance identifier
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable instance name
    /// </summary>
    public string InstanceName { get; set; } = string.Empty;

    /// <summary>
    /// Type of the instance
    /// </summary>
    public Type? InstanceType { get; set; }

    /// <summary>
    /// Instance key for keyed service instances (optional)
    /// </summary>
    public string? InstanceKey { get; set; }

    /// <summary>
    /// Group ID for grouping related instances (optional)
    /// </summary>
    public string? GroupId { get; set; }

    #endregion

    #region State

    /// <summary>
    /// Current state of the instance (can be any type)
    /// </summary>
    public object? CurrentState { get; set; }

    /// <summary>
    /// Timestamp when the current state was entered
    /// </summary>
    public DateTime StateChangedAt { get; set; }

    /// <summary>
    /// Timestamp when this instance was registered
    /// </summary>
    public DateTime RegisteredAt { get; set; }

    #endregion

    #region Statistics

    /// <summary>
    /// Total number of state changes that have occurred
    /// </summary>
    public int TotalStateChanges { get; set; }

    /// <summary>
    /// Total number of exceptions that have occurred
    /// </summary>
    public int TotalExceptions { get; set; }

    /// <summary>
    /// Current number of history entries
    /// </summary>
    public int HistoryCount { get; set; }

    /// <summary>
    /// Count of entries with exceptions
    /// </summary>
    public int ExceptionCount { get; set; }

    /// <summary>
    /// Whether there are any exceptions in history
    /// </summary>
    public bool HasExceptions { get; set; }

    #endregion

    #region Log Level & Health State

    /// <summary>
    /// Current log level for this instance (null if not mapped)
    /// </summary>
    public LogLevel? CurrentLogLevel { get; set; }

    /// <summary>
    /// Whether this instance is unhealthy (Warning/Error/Critical)
    /// </summary>
    public bool IsUnhealthy { get; set; }

    /// <summary>
    /// Computed health state
    /// </summary>
    public HealthState HealthState
    {
        get
        {
            if (CurrentLogLevel == null) return HealthState.Unknown;
            return IsUnhealthy ? HealthState.Unhealthy : HealthState.Healthy;
        }
    }

    #endregion

    #region Display Properties

    /// <summary>
    /// Formatted state value (handles object? with JSON/ToString)
    /// </summary>
    public string StateDisplayText => FormatStateDisplay(CurrentState);

    /// <summary>
    /// Type of current state
    /// </summary>
    public string StateTypeDisplay => CurrentState?.GetType().GetCleanName() ?? "-";

    /// <summary>
    /// Full type name using GetCleanFullName()
    /// </summary>
    public string InstanceTypeFullName => InstanceType?.GetCleanFullName() ?? "-";

    /// <summary>
    /// MudBlazor color for log level
    /// </summary>
    public Color LogLevelColor => ObservableInstanceDisplayMapping.GetLogLevelColor(CurrentLogLevel);

    /// <summary>Gets theme-inheriting CSS for the current log level's native icon.</summary>
    public string LogLevelIconStyle => ObservableInstanceDisplayMapping.IconStyle(LogLevelColor);

    /// <summary>
    /// Icon for log level
    /// </summary>
    public string LogLevelIcon => ObservableInstanceDisplayMapping.GetLogLevelIcon(CurrentLogLevel);

    /// <summary>
    /// MudBlazor color for health state
    /// </summary>
    public Color HealthStateColor => ObservableInstanceDisplayMapping.GetHealthStateColor(HealthState);

    /// <summary>Gets theme-inheriting CSS for the current health state's native icon.</summary>
    public string HealthStateIconStyle => ObservableInstanceDisplayMapping.IconStyle(HealthStateColor);

    /// <summary>
    /// Icon for health state
    /// </summary>
    public string HealthStateIcon => ObservableInstanceDisplayMapping.GetHealthStateIcon(HealthState);

    #endregion

    #region Color Properties

    /// <summary>
    /// Color based on exception status
    /// </summary>
    public Color ExceptionStatusColor => HasExceptions ? Color.Error : Color.Success;

    #endregion

    #region Computed Properties

    /// <summary>
    /// Whether this instance has a group
    /// </summary>
    public bool HasGroup => !string.IsNullOrEmpty(GroupId);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Formats state value for display (handles object? types)
    /// </summary>
    private static string FormatStateDisplay(object? state)
    {
        if (state == null) return "-";

        // Handle primitive types and common types
        if (state is string or int or long or bool or DateTime or Guid)
            return state.ToString() ?? "-";

        // Handle enums
        if (state.GetType().IsEnum)
            return state.ToString() ?? "-";

        // For complex objects, serialize to JSON (max 200 chars)
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = false,
                MaxDepth = 3
            });
            return json.Length > 200 ? json[..200] + "..." : json;
        }
        catch
        {
            return state.GetType().GetCleanName();  // Fallback to type name
        }
    }

    #endregion
}
