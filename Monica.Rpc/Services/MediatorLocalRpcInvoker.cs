using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Rpc.Abstractions;

namespace Monica.Rpc.Services;

/// <summary>Default Local adapter for hosts that already own the execution scope.</summary>
internal sealed class MediatorLocalRpcInvoker(IMediator mediator) : ILocalRpcInvoker
{
    public Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
        where TResult : class, IRemoteResultEnvelope<TResult>
        => mediator.Send(request, cancellationToken);
}
