using Monica.Core.Mediator;

namespace Monica.Application.Abstractions;

/// <summary>A read-only application request returning a typed result envelope.</summary>
/// <typeparam name="TResponse">The result payload.</typeparam>
/// <remarks>Uses no automatic root transaction; an explicit transaction remains read-only.</remarks>
public interface IQuery<TResponse> : IResultRequest<TResponse>, IQueryRequest;

/// <summary>A read-only application request returning a result envelope without a payload.</summary>
public interface IQuery : IResultRequest, IQueryRequest;
