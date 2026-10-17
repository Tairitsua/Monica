using System.Net;
using System.Text.Json;

namespace Monica.Rpc.Models;

/// <summary>Provides bounded provider evidence valid only during response classification.</summary>
/// <param name="StatusCode">Actual upstream HTTP status.</param>
/// <param name="Body">Borrowed parsed JSON. Implementations must not retain it.</param>
public readonly record struct RemoteResponseContext(HttpStatusCode StatusCode, JsonElement Body);
