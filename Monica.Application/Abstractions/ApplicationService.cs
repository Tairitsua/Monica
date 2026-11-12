using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;

/// <summary>
/// Marks shared application-service infrastructure, including request handlers and CRUD services.
/// </summary>
public interface IApplicationService 
{

}
/// <summary>
/// Provides host-bound logging and mapping services to application boundaries.
/// </summary>
/// <remarks>
/// Resolve instances through Monica dependency injection so their host service provider is assigned.
/// Use <see cref="ApplicationRequestHandler{TRequest,TResponse}"/> for a single mediator request or a CRUD
/// application service for a multi-method HTTP boundary. This base alone does not define an execution boundary.
/// </remarks>
public abstract class ApplicationService :
    ServiceBase,
    IApplicationService,
    ITransientDependency
{
}
