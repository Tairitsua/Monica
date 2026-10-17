using Monica.Rpc.Models;

namespace Monica.Rpc.Exceptions;

/// <summary>Carries classified dependency evidence to the result boundary without exposing exception prose.</summary>
/// <param name="failure">The typed failure description.</param>
/// <param name="innerException">Optional original cause for internal diagnosis.</param>
public sealed class RemoteCallException(RemoteCallFailure failure, Exception? innerException = null)
    : Exception(failure.Code, innerException)
{
    /// <summary>Gets classified public semantics and operator-only transport evidence.</summary>
    public RemoteCallFailure Failure { get; } = failure;
}
