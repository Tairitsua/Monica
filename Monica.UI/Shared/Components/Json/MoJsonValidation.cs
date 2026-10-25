namespace Monica.UI.Shared.Components.Json;

/// <summary>
/// Syntax evidence for a JSON draft, independent of application schema validation.
/// Error positions are one-based; the byte position counts UTF-8 bytes rather than characters.
/// </summary>
/// <param name="IsValid">Whether the complete draft is valid, strict JSON.</param>
/// <param name="IsEmpty">Whether the draft contains only whitespace.</param>
/// <param name="Error">The parser diagnostic, or null for a valid or empty draft.</param>
/// <param name="LineNumber">One-based error line, when available.</param>
/// <param name="BytePositionInLine">One-based UTF-8 byte position, when available.</param>
public sealed record MoJsonValidation(bool IsValid, bool IsEmpty, string? Error = null,
    long? LineNumber = null, long? BytePositionInLine = null);
