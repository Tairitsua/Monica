namespace Monica.Core.Results.Abstractions;

/// <summary>
/// Defines a result envelope that can safely represent failures raised while consuming a remote response.
/// </summary>
/// <typeparam name="TSelf">The concrete result-envelope type.</typeparam>
/// <remarks>
/// Remote clients use this factory only when a response cannot be decoded into a healthy envelope. Implementations
/// must create a valid instance through their normal constructors so domain invariants remain intact.
/// </remarks>
public interface IRemoteResultEnvelope<TSelf> : IResultEnvelope
    where TSelf : class, IRemoteResultEnvelope<TSelf>
{
    /// <summary>
    /// Creates a valid envelope for a failure raised by the remote transport or response decoder.
    /// </summary>
    /// <param name="status">The failure status.</param>
    /// <param name="message">The user-facing failure message.</param>
    /// <returns>A valid failure envelope of the concrete type.</returns>
    static abstract TSelf CreateRemoteFailure(ResultStatus status, string message);
}
