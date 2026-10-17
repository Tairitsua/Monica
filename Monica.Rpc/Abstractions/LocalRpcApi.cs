using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.DependencyInjection.Abstractions;

namespace Monica.Rpc.Abstractions;

/// <summary>
/// Base type for generated in-process RPC clients used by modular monolith deployments.
/// </summary>
/// <remarks>
/// Generated Local implementations use ILocalRpcInvoker. Application hosts establish an outer operation scope
/// and reuse it for nested calls. No HTTP serialization or remote transport is involved.
/// </remarks>
public abstract class LocalRpcApi(ICachedServiceProvider serviceProvider) : RpcApi(serviceProvider)
{
    /// <summary>
    /// Gets the mediator used to dispatch local RPC requests.
    /// </summary>
    protected IMediator Mediator => CachedServiceProvider.GetRequiredService<IMediator>();

    /// <summary>Invokes through the host-owned boundary instead of retaining a circuit-owned mediator.</summary>
    protected Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
        where TResult : class, IRemoteResultEnvelope<TResult>
        => CachedServiceProvider.GetRequiredService<ILocalRpcInvoker>().InvokeAsync(request, cancellationToken);
}
