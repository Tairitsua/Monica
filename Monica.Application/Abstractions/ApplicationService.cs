using Monica.Core.Mediator;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;

/// <summary>
/// Mark as application service
/// </summary>
public interface IApplicationService 
{

}
/// <summary>
/// Base class for application services, providing common properties and methods.
/// </summary>
public abstract class ApplicationService :
    ServiceBase,
    IApplicationService,
    ITransientDependency
{
}

/// <summary>
/// Base class for custom application services with a specific handler, request, and response.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResponse">The type of the response.</typeparam>
public abstract class CustomApplicationService<TRequest, TResponse> :
    ApplicationService, IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles the specified request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the response.</returns>
    public abstract Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for application services with a specific handler, request, and response wrapped in a <see cref="Res{T}"/>.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
/// <typeparam name="TResponse">The type of the response.</typeparam>
public abstract class ApplicationService<TRequest, TResponse> :
    CustomApplicationService<TRequest, Res<TResponse>>
    where TRequest : IResultRequest<TResponse>
{
}

/// <summary>
/// Base class for application services with a specific handler, request, and response wrapped in a <see cref="Res"/>.
/// </summary>
/// <typeparam name="TRequest">The type of the request.</typeparam>
public abstract class ApplicationService<TRequest> :
    CustomApplicationService<TRequest, Res>
    where TRequest : IResultRequest
{
}
