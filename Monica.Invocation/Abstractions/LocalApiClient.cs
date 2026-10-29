using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Invocation.Services;

namespace Monica.Invocation.Abstractions;

/// <summary>
/// Base type for explicitly selected in-process application API clients.
/// </summary>
/// <remarks>
/// Generated Local implementations use ILocalApiInvoker. Application hosts establish an outer operation scope
/// and reuse it for nested calls. No HTTP serialization or remote transport is involved.
/// </remarks>
public abstract class LocalApiClient(ICachedServiceProvider serviceProvider) : ApiClient(serviceProvider)
{
    /// <summary>
    /// Gets the mediator used to dispatch local API requests.
    /// </summary>
    protected IMediator Mediator => CachedServiceProvider.GetRequiredService<IMediator>();

    /// <summary>Invokes through the host-owned boundary instead of retaining a circuit-owned mediator.</summary>
    protected Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default)
        where TResult : class, IRemoteResultEnvelope<TResult>
        => ApiTransactionPolicy.ExecuteAsync(CachedServiceProvider, Transaction,
            () => CachedServiceProvider.GetRequiredService<ILocalApiInvoker>().InvokeAsync(request, cancellationToken, Transaction),
            isReadOnly: request is IQueryRequest, operation: request?.GetType().FullName);
}
