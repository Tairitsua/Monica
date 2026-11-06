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
/// Request contract whose full mediator response is <see cref="Result{T}"/>.
/// </summary>
/// <typeparam name="TData">The data payload carried by the result envelope.</typeparam>
public interface IResultRequest<TData> : IRequest<Result<TData>>, IResultRequestBase
{
    
}

/// <summary>
/// Request contract whose full mediator response is <see cref="Result"/> without a data payload.
/// </summary>
public interface IResultRequest : IRequest<Result>, IResultRequestBase
{

}
