using Monica.Core.Mediator;

namespace Monica.Application.Abstractions;

/// <summary>An application command returning a typed result envelope.</summary>
/// <typeparam name="TData">The data payload in the full mediator response <c>Result&lt;TData&gt;</c>.</typeparam>
/// <remarks>Uses an automatic local transaction unless its execution entry explicitly overrides it.</remarks>
public interface ICommand<TData> : IResultRequest<TData>, ICommandRequest;

/// <summary>An application command returning a result envelope without a payload.</summary>
public interface ICommand : IResultRequest, ICommandRequest;
