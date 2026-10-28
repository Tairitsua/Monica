using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Models;

namespace Monica.Invocation.Abstractions;

/// <summary>Routes one generated application call before entering its Local or HTTP execution boundary.</summary>
/// <remarks>Scoped to the caller. Placement is fixed at startup; execution failures never trigger another delivery path.</remarks>
public interface IApplicationApiInvoker
{
    /// <summary>Invokes the operation with its shared HTTP mapping and caller cancellation.</summary>
    /// <remarks>
    /// Shared is the default and rejects remote dispatch during an active local transaction. Independent isolates
    /// local execution in a fresh operation and leaves remote execution owned by the receiver. It never bypasses role routing.
    /// </remarks>
    Task<TResponse> InvokeAsync<TResponse>(IRequest<TResponse> request, ApiHttpEndpoint endpoint,
        CancellationToken cancellationToken = default, ApiTransaction transaction = ApiTransaction.Shared)
        where TResponse : class, IRemoteResultEnvelope<TResponse>;
}
