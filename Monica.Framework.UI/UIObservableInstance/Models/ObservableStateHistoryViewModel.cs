using System.Text.Json;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.Tool.Extensions;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Framework.UI.UIObservableInstance.Models;

/// <summary>
/// UI-friendly view model for ObservableStateEntry entries
/// </summary>
public class ObservableStateHistoryViewModel
{
    #region Core Properties

    /// <summary>
    /// Timestamp when this state change occurred
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Per-instance sequence number for deterministic ordering.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Previous state before this change
    /// </summary>
    public object? PreviousState { get; set; }

    /// <summary>
    /// Current state after this change
    /// </summary>
    public object? CurrentState { get; set; }

    /// <summary>
    /// Descriptive message about this state change
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Exception associated with this state change (if any)
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Log level associated with this state change (if mapped)
    /// </summary>
    public LogLevel? LogLevel { get; set; }

    #endregion

    #region Flags

    /// <summary>
    /// Whether this is an exception entry
    /// </summary>
    public bool IsException => Exception != null;

    /// <summary>
    /// Whether this is a state transition (state changed)
    /// </summary>
    public bool IsStateTransition => !Equals(PreviousState, CurrentState);

    #endregion

    #region Display Properties

    /// <summary>
    /// Formatted previous state
    /// </summary>
    public string PreviousStateDisplay => FormatStateDisplay(PreviousState);

    /// <summary>
    /// Formatted current state
    /// </summary>
    public string CurrentStateDisplay => FormatStateDisplay(CurrentState);

    /// <summary>
    /// Exception message (if exception exists)
    /// </summary>
    public string ExceptionMessage => Exception?.GetMessageRecursively() ?? string.Empty;

    /// <summary>
    /// Exception type name (if exception exists)
    /// </summary>
    public string ExceptionType => Exception?.GetType().GetCleanName() ?? string.Empty;

    /// <summary>
    /// Full exception details including inner exceptions and stack traces (if exception exists)
    /// </summary>
    public string ExceptionStackTrace => Exception?.ToString() ?? string.Empty;

    /// <summary>
    /// Exception summary for display (type + message)
    /// </summary>
    public string ExceptionSummary
    {
        get
        {
            if (Exception == null) return string.Empty;

            var message = Exception.GetMessageRecursively();
            if (message.Length > 100)
                message = message[..100] + "...";

            return $"{ExceptionType}: {message}";
        }
    }

    /// <summary>
    /// Log level color
    /// </summary>
    public Color LogLevelColor => ObservableInstanceDisplayMapping.GetLogLevelColor(LogLevel);

    /// <summary>
    /// Log level icon
    /// </summary>
    public string LogLevelIcon => ObservableInstanceDisplayMapping.GetLogLevelIcon(LogLevel);

    #endregion

    #region Color Properties

    /// <summary>
    /// Color based on log level (preferred) or entry type fallback
    /// </summary>
    public Color HistoryEntryColor
    {
        get
        {
            // Prefer log level color if available
            if (LogLevel.HasValue)
                return ObservableInstanceDisplayMapping.GetLogLevelColor(LogLevel);

            // Fallback to exception/transition logic
            return (IsException, IsStateTransition) switch
            {
                (true, true) => Color.Warning,   // State change with exception
                (true, false) => Color.Error,    // Exception without state change
                (false, true) => Color.Info,     // Normal state change
                (false, false) => Color.Default  // Message only
            };
        }
    }

    /// <summary>
    /// Icon for this history entry
    /// </summary>
    public string HistoryEntryIcon => (IsException, IsStateTransition) switch
    {
        (true, true) => MoIcons.Warning,     // State change with exception
        (true, false) => MoIcons.Error,           // Exception without state change
        (false, true) => MoIcons.ArrowLeftRight,       // Normal state change
        (false, false) => MoIcons.Info           // Message only
    };

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
