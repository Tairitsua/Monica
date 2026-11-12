using Monica.Core.Results;
using Monica.Core.Results.Abstractions;

namespace Monica.Core.Execution;

/// <summary>Classifies returned outcomes consistently at local transaction and API invocation boundaries.</summary>
public static class ExecutionOutcomePolicy
{
    /// <summary>Determines whether a returned outcome requires rollback of its shared operation.</summary>
    /// <param name="result">The returned result, including custom result envelopes.</param>
    /// <param name="isReadOnly">Whether the request explicitly declares Query semantics.</param>
    /// <returns>True for explicit rollback outcomes, command failures, and query execution failures.</returns>
    /// <remarks>
    /// Queries may return a handled client rejection without invalidating their caller's transaction.
    /// Rate limiting, dependency/server faults, undefined statuses and explicit rollback outcomes remain
    /// failures. Exceptions and cancellation are handled by the boundary; this method never clears an
    /// existing rollback-only flag. Infrastructure exceptions must not be disguised as business rejections.
    /// </remarks>
    public static bool RequiresRollback(object? result, bool isReadOnly = false)
    {
        if (result is IExecutionOutcome { ShouldRollback: true }) return true;
        if (result is not IResultEnvelope envelope) return false;
        // Invalid envelopes remain rollback outcomes even though public status predicates reject them.
        if (envelope.Status is ResultStatus.Ok or ResultStatus.Created) return false;
        return !isReadOnly || envelope.Status is not (ResultStatus.BadRequest or ResultStatus.Unauthorized
            or ResultStatus.Forbidden or ResultStatus.NotFound or ResultStatus.Conflict
            or ResultStatus.PayloadTooLarge or ResultStatus.UnsupportedMediaType);
    }
}
