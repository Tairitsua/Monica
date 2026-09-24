using System.Text.Json;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.Results;

/// <summary>Applies the public result contract at HTTP and remote-call boundaries.</summary>
public static class ResultPresentationExtensions
{
    private static readonly string[] DIAGNOSTIC_KEYS =
        ["originResponse", "response", "request", "deserializationError", "exception", "diagnostics", "detail", "chain", "chain_error", "remoteService"];

    /// <summary>
    /// Removes reserved technical metadata and fills missing failure presentation. Domain data and public
    /// application metadata are retained. An existing error must use <see cref="ResultError"/>; mixed producers
    /// are a local contract defect. Capture the trace once at the owning boundary and use it in diagnostics too.
    /// </summary>
    /// <param name="result">The result envelope to prepare.</param>
    /// <param name="json">Serializer options used to project typed error metadata.</param>
    /// <param name="messages">Provider of user-facing failure messages.</param>
    /// <param name="traceId">The correlation identifier captured at the owning boundary.</param>
    /// <param name="service">Optional owning service name stamped onto synthesized errors.</param>
    /// <param name="operation">Optional owning operation name stamped onto synthesized errors.</param>
    /// <param name="exposeReservedDiagnostics">
    /// Retains reserved diagnostic members in the response. Only trusted development hosts may enable this;
    /// remote-call boundaries apply the same host diagnostic policy before forwarding.
    /// </param>
    public static T PrepareForPresentation<T>(this T result, JsonSerializerOptions json,
        IResultErrorMessageProvider messages, string traceId, string? service = null, string? operation = null,
        bool exposeReservedDiagnostics = false)
        where T : IResultEnvelope
    {
        if (result.Metadata is IDictionary<string, object?> metadata)
        {
            if (!exposeReservedDiagnostics)
                foreach (var key in metadata.Keys.Where(IsDiagnosticKey).ToArray()) metadata.Remove(key);
            if (metadata.ContainsKey("error") && !result.TryGetError(json, out _))
                throw new InvalidOperationException("The reserved metadata.error member must be a ResultError.");
            if (result.TryGetError(json, out var declaredError))
                result.SetError(declaredError); // Project JSON into the public model; discard undeclared technical members.
        }

        if (result.IsOk()) return result;
        if (!result.TryGetError(json, out var error))
        {
            error = new ResultError(result.Status switch
            {
                ResStatus.InternalError => ResultErrorCodes.UnexpectedError,
                ResStatus.Unauthorized => ResultErrorCodes.Unauthorized,
                ResStatus.Forbidden => ResultErrorCodes.Forbidden,
                _ => ResultErrorCodes.OperationFailed
            }, traceId, service, operation);
            result.SetError(error);
        }
        if (string.IsNullOrWhiteSpace(result.Message)) result.Message = messages.GetMessage(error);
        return result;
    }

    private static bool IsDiagnosticKey(string key) => DIAGNOSTIC_KEYS.Any(reserved =>
    {
        if (key.Equals(reserved, StringComparison.OrdinalIgnoreCase)) return true;
        if (!key.StartsWith(reserved, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = key.AsSpan(reserved.Length);
        if (suffix.StartsWith("_")) suffix = suffix[1..];
        return !suffix.IsEmpty && !suffix.ContainsAnyExceptInRange('0', '9');
    });
}
