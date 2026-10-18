namespace Monica.Invocation.Models;

/// <summary>
/// Selects which API client transport implementation should be registered for a contract.
/// </summary>
public enum ApiClientTransportKind
{
    /// <summary>
    /// Registers HTTP-based API implementations.
    /// </summary>
    Http = 0,

    /// <summary>
    /// Registers in-process mediator-based API implementations.
    /// </summary>
    Local = 1,

    /// <summary>
    /// Reserves gRPC delivery. Generation and registration reject it until an adapter is implemented.
    /// </summary>
    Grpc = 2
}
