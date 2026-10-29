using Monica.Core.Mediator;

namespace Monica.Application.Abstractions;

/// <summary>An application command returning a typed result envelope.</summary>
/// <typeparam name="TResponse">The result payload.</typeparam>
/// <remarks>Uses an automatic local transaction unless its execution entry explicitly overrides it.</remarks>
public interface ICommand<TResponse> : IResultRequest<TResponse>, ICommandRequest;

/// <summary>An application command returning a result envelope without a payload.</summary>
public interface ICommand : IResultRequest, ICommandRequest;
