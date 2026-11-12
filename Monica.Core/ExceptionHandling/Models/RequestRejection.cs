using Monica.Core.Results;

namespace Monica.Core.ExceptionHandling.Models;

/// <summary>Contains safe request-rejection information before an HTTP adapter constructs its result.</summary>
/// <param name="Status">Existing result status, including retained application-specific values.</param>
/// <param name="Message">Safe user-facing description.</param>
/// <param name="Error">Typed public error details.</param>
public sealed record RequestRejection(ResultStatus Status, string Message, ResultError Error)
{
    /// <summary>Creates the result at a host-facing response boundary.</summary>
    public Result ToResult() => Result.Failure(Message, Status).SetError(Error);
}
