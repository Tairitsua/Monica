using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Results.Abstractions;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Models;

namespace Monica.Invocation.Services;

/// <summary>Default Local adapter for hosts that already own the execution scope.</summary>
internal sealed class MediatorLocalApiInvoker(IServiceProvider services, IServiceScopeFactory scopes, ExecutionScopeContext operation) : ILocalApiInvoker
{
    public async Task<TResult> InvokeAsync<TResult>(IRequest<TResult> request, CancellationToken cancellationToken = default,
        ApiTransaction transaction = ApiTransaction.Shared)
        where TResult : class, IRemoteResultEnvelope<TResult>
    {
        ApiTransactionPolicy.Validate(transaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (transaction == ApiTransaction.Shared)
            return await (operation.Services ?? services).GetRequiredService<IMediator>().Send(request, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        using var lease = operation.Enter(scope.ServiceProvider);
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request, cancellationToken);
    }
}
