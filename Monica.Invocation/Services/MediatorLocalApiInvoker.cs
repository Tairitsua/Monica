using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Abstractions;

namespace Monica.Invocation.Services;

/// <summary>Default Local adapter for hosts that already own the execution scope.</summary>
internal sealed class MediatorLocalApiInvoker(IMediator mediator) : ILocalApiInvoker
{
    public Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
        where TResult : class, IRemoteResultEnvelope<TResult>
        => mediator.Send(request, cancellationToken);
}
