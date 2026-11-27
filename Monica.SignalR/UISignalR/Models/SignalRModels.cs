using Monica.SignalR.Models;

namespace Monica.SignalR.UISignalR.Models;

/// <summary>
/// Represents a callable hub method in the debug UI.
/// </summary>
public sealed class HubMethodInfo
{
    /// <summary>
    /// Gets or sets the hub method name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name rendered by the UI.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the method parameter metadata.
    /// </summary>
    public List<SignalRHubParameterInfo> Args { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the method listener is currently enabled.
    /// </summary>
    public bool IsListening { get; set; }

    /// <summary>
    /// Gets or sets the number of received messages for this method.
    /// </summary>
    public int ReceivedCount { get; set; }
}

/// <summary>
/// Represents a log entry emitted by the debug UI.
/// </summary>
public sealed class SignalRMessage
{
    /// <summary>Gets the stable session-local identity used by trace evidence links.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the log source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the primary message content.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets optional detail text.
    /// </summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the message type.
    /// </summary>
    public MessageType Type { get; set; }

    /// <summary>
    /// Gets or sets the timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the message represents an error.
    /// </summary>
    public bool IsError { get; set; }
}

/// <summary>
/// Distinguishes the kinds of messages emitted by the debug UI.
/// </summary>
public enum MessageType
{
    /// <summary>
    /// A message sent from the browser to the hub.
    /// </summary>
    Sent,

    /// <summary>
    /// A message received from the hub.
    /// </summary>
    Received,

    /// <summary>
    /// A system-level informational message.
    /// </summary>
    System,

    /// <summary>
    /// A successful operation message.
    /// </summary>
    Success,

    /// <summary>
    /// A failed operation message.
    /// </summary>
    Error,

    /// <summary>
    /// A neutral informational message.
    /// </summary>
    Info
}

/// <summary>
/// Represents the current browser-side SignalR connection state.
/// </summary>
public sealed class SignalRConnectionState
{
    /// <summary>
    /// Gets or sets the connection status text reported by the JavaScript client.
    /// </summary>
    public string Status { get; set; } = "Disconnected";

    /// <summary>
    /// Gets or sets the current connection identifier.
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a connection attempt is in progress.
    /// </summary>
    public bool IsConnecting { get; set; }

    /// <summary>
    /// Gets or sets the total number of received hub messages.
    /// </summary>
    public int TotalReceivedMessages { get; set; }

    /// <summary>
    /// Gets a value indicating whether the connection is currently established.
    /// </summary>
    public bool IsConnected => Status == "Connected";
}

/// <summary>
/// Represents a method invocation parameter entered by the user.
/// </summary>
public sealed class MethodCallParameter
{
    /// <summary>
    /// Gets or sets the parameter name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the raw parameter value typed by the user.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target CLR type name.
    /// </summary>
    public string Type { get; set; } = string.Empty;
}
