using System.Text.Json.Serialization;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.Execution;

/// <summary>Describes the first failure that made a local transaction unable to commit.</summary>
/// <remarks>
/// The transaction owner captures this immutable snapshot once and retains it until its scope is disposed.
/// It contains no request, result payload, SQL, or parameters. Exception messages can contain sensitive details;
/// expose them only through the host's diagnostic visibility policy.
/// </remarks>
public sealed record ExecutionTransactionFailure(string Source, string Reason)
{
    /// <summary>Gets the original exception instance, preserving its stack and inner exceptions.</summary>
    [JsonIgnore]
    public Exception? Exception { get; init; }

    /// <summary>Gets the failed result's status, when failure was reported as a result.</summary>
    public ResStatus? ResultStatus { get; init; }

    /// <summary>Gets the failed operation's display name, or null outside a known execution.</summary>
    public string? Operation { get; init; }

    /// <summary>Gets the stable execution operation key, when available.</summary>
    public string? OperationKey { get; init; }

    /// <summary>Gets the failed execution's invocation identity, when available.</summary>
    public Guid? InvocationId { get; init; }

    /// <summary>Gets the UTC time at which the failure was recorded.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the active trace identity at the first failure, when available.</summary>
    public string? TraceId { get; init; }

    /// <summary>Gets the active span identity at the first failure, when available.</summary>
    public string? SpanId { get; init; }

    /// <summary>Gets the failing context's type name, when a database operation failed.</summary>
    public string? DbContextType { get; init; }

    /// <summary>Gets the provider command correlation identity, without capturing command text.</summary>
    public Guid? CommandId { get; init; }

    /// <summary>Creates a failure from an exception without changing or wrapping the original exception.</summary>
    public static ExecutionTransactionFailure FromException(string source, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(source, exception.Message) { Exception = exception };
    }

    /// <summary>Snapshots a failed result's status and message without retaining its payload or metadata.</summary>
    /// <remarks>Call only after the execution outcome policy determines that rollback is required.</remarks>
    public static ExecutionTransactionFailure FromResult<T>(string source, T result)
        => result is IResultEnvelope envelope
            ? new(source, envelope.Message ?? "The operation returned a failed result.") { ResultStatus = envelope.Status }
            : new(source, "The execution outcome explicitly requires rollback.");
}
