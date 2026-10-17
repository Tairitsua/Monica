using Monica.Application.Abstractions;

namespace Monica.Rpc.Models;

/// <summary>A result request with optional HTTP caller headers. New transport-neutral contracts can implement IResultRequest directly.</summary>
public record RpcRequest<TResponse> : RpcRequest, IResultRequest<TResponse>;
