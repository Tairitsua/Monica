namespace Monica.Core.Mediator;

/// <summary>Declares a command independently of its response type, name, or transport.</summary>
/// <remarks>
/// Combine with <see cref="IRequest{TResponse}"/> for custom result contracts. Commands default to an
/// automatic local transaction and retain shared rollback semantics. A request cannot also implement
/// <see cref="IQueryRequest"/>.
/// </remarks>
public interface ICommandRequest;
