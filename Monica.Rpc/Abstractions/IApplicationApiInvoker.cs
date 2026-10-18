using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Rpc.Models;

namespace Monica.Rpc.Abstractions;

/// <summary>Routes one generated application call before entering its Local or HTTP execution boundary.</summary>
/// <remarks>Scoped to the caller. Placement is fixed at startup; execution failures never trigger another delivery path.</remarks>
public interface IApplicationApiInvoker
{
    /// <summary>Invokes the operation with its shared HTTP mapping and caller cancellation.</summary>
    Task<TResponse> InvokeAsync<TResponse>(IRequest<TResponse> request, RpcHttpEndpoint endpoint,
        CancellationToken cancellationToken = default)
        where TResponse : class, IRemoteResultEnvelope<TResponse>;
}
