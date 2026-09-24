using System.Text.Json;

namespace Monica.Core.ExceptionHandling.Models;

/// <summary>
/// Separates a transport exception's local diagnostic message from a parsed remote response. The shared
/// diagnostic collector normalizes the response and applies its own size and depth limits before exposure.
/// </summary>
/// <param name="Message">The local transport message with the extracted response body removed.</param>
/// <param name="Response">A detached JSON object containing the complete remote response.</param>
/// <param name="Transport">The provider's stable transport identifier, such as <c>dapr-actor</c>.</param>
/// <param name="StatusCode">The HTTP status observed at the remote boundary, when available.</param>
public sealed record RemoteExceptionPayload(string Message, JsonElement Response, string Transport,
    int? StatusCode = null);
