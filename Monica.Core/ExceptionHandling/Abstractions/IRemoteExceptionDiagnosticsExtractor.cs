using System.Diagnostics.CodeAnalysis;
using Monica.Core.ExceptionHandling.Models;

namespace Monica.Core.ExceptionHandling.Abstractions;

/// <summary>
/// Extracts a structured remote response from an exception owned by a transport provider. Implementations
/// are singleton services and must remain thread-safe, bounded, synchronous, and free of network calls.
/// Extraction enriches diagnostic output only; it does not classify the public failure or change its status.
/// </summary>
public interface IRemoteExceptionDiagnosticsExtractor
{
    /// <summary>
    /// Recognizes a provider-owned failure and separates its remote response from its local message.
    /// Unrecognized, malformed, or oversized payloads must return <see langword="false" /> without throwing.
    /// </summary>
    /// <param name="exception">The local exception to inspect, without traversing its inner exceptions.</param>
    /// <param name="payload">The extracted payload, whose JSON remains valid after this method returns.</param>
    /// <returns>Whether the provider recognized and extracted a complete remote response.</returns>
    bool TryExtract(Exception exception, [NotNullWhen(true)] out RemoteExceptionPayload? payload);
}
