using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.Core.Mediator;

/// <summary>
/// Default request dispatcher for Monica request handlers.
/// </summary>
public sealed class Mediator(IServiceProvider serviceProvider) : IMediator
{
    private static readonly ConcurrentDictionary<(Type RequestType, Type ResponseType), Func<Mediator, object, CancellationToken, Task<object?>>> _dispatcherCache = new();

    /// <inheritdoc />
    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dispatcher = _dispatcherCache.GetOrAdd(
            (request.GetType(), typeof(TResponse)),
            static key => CreateDispatcher(key.RequestType, key.ResponseType));

        var response = await dispatcher(this, request, cancellationToken).ConfigureAwait(false);
        return (TResponse)response!;
    }

    private static Func<Mediator, object, CancellationToken, Task<object?>> CreateDispatcher(
        Type requestType,
        Type responseType)
    {
        var mediatorParameter = Expression.Parameter(typeof(Mediator), "mediator");
        var requestParameter = Expression.Parameter(typeof(object), "request");
        var cancellationTokenParameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        var executeMethod = typeof(Mediator)
            .GetMethod(nameof(Execute), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(requestType, responseType);

        var executeCall = Expression.Call(
            mediatorParameter,
            executeMethod,
            Expression.Convert(requestParameter, requestType),
            cancellationTokenParameter);

        var boxResultMethod = typeof(Mediator)
            .GetMethod(nameof(BoxResultAsync), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(responseType);

        var boxedCall = Expression.Call(boxResultMethod, executeCall);

        return Expression.Lambda<Func<Mediator, object, CancellationToken, Task<object?>>>(
            boxedCall,
            mediatorParameter,
            requestParameter,
            cancellationTokenParameter).Compile();
    }

    private async Task<TResponse> Execute<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        serviceProvider.GetService<ITypeActivationPolicy>()?.EnsureCanExecuteRequest(typeof(TRequest));
        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        var handlerType = handler.GetType();
        var readOnly = request is IQueryRequest;
        var descriptor = ExecutionDescriptor.ForInterface<TRequest, TResponse>(
            MediatorExecutionPoints.Request,
            handlerType,
            typeof(IRequestHandler<TRequest, TResponse>),
            isBusinessOperation: true,
            transactionMode: readOnly ? ExecutionTransactionMode.None : ExecutionTransactionMode.Automatic);

        return await serviceProvider
            .GetRequiredService<IExecutionPipeline>()
            .ExecuteAsync(
                descriptor,
                request,
                handler,
                () => handler.Handle(request, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<object?> BoxResultAsync<TResponse>(Task<TResponse> responseTask)
    {
        return await responseTask.ConfigureAwait(false);
    }
}
