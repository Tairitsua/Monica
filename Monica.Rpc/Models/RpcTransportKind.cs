namespace Monica.Rpc.Models;

/// <summary>
/// Selects which RPC client transport implementation should be registered for a contract.
/// </summary>
public enum RpcTransportKind
{
    /// <summary>
    /// Registers HTTP-based RPC implementations.
    /// </summary>
    Http = 0,

    /// <summary>
    /// Registers in-process mediator-based RPC implementations.
    /// </summary>
    Local = 1,

    /// <summary>
    /// Registers gRPC-based RPC implementations.
    /// </summary>
    Grpc = 2
}
