using Monica.Core.Mediator;
using Monica.Core.Results;

namespace Monica.Application.Abstractions;
/// <summary>
/// Base interface for request contracts; this type should not be used directly.
/// </summary>
public interface IResultRequestBase
{

}

/// <summary>
/// Request interface that returns a typed result.
/// </summary>
/// <typeparam name="TRequest">The response type for the request.</typeparam>
public interface IResultRequest<TRequest> : IRequest<Res<TRequest>>, IResultRequestBase
{
    
}

/// <summary>
/// Simple request interface.
/// </summary>
public interface IResultRequest : IRequest<Res>, IResultRequestBase
{

}