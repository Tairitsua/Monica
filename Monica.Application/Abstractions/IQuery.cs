using Monica.Core.Mediator;

namespace Monica.Application.Abstractions;

/// <summary>A read-only application request returning a typed result envelope.</summary>
/// <typeparam name="TData">The data payload in the full mediator response <c>Result&lt;TData&gt;</c>.</typeparam>
/// <remarks>Uses no automatic root transaction; an explicit transaction remains read-only.</remarks>
public interface IQuery<TData> : IResultRequest<TData>, IQueryRequest;

/// <summary>A read-only application request returning a result envelope without a payload.</summary>
public interface IQuery : IResultRequest, IQueryRequest;
