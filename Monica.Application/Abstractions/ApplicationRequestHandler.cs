using Monica.Core.Mediator;
using Monica.Core.Results;

namespace Monica.Application.Abstractions;

/// <summary>
/// Defines one application use case executed through Monica's mediator pipeline.
/// </summary>
/// <remarks>
/// Resolve handlers through Monica dependency injection and invoke them through the mediator so execution
/// policies and transaction boundaries apply. The response is explicit: use <see cref="Result{T}"/>,
/// <see cref="Result"/>, or the request's custom response contract without implicit result wrapping.
/// </remarks>
/// <typeparam name="TRequest">The request contract accepted by this handler.</typeparam>
/// <typeparam name="TResponse">The exact full mediator response, including any result envelope.</typeparam>
public abstract class ApplicationRequestHandler<TRequest, TResponse> :
    ApplicationService, IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Executes the application use case for the specified request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Signals cancellation of the request execution.</param>
    /// <returns>The exact response declared by the request contract.</returns>
    public abstract Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}
