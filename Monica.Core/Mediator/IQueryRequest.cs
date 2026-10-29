namespace Monica.Core.Mediator;

/// <summary>Declares a read-only request independently of its response type, name, or transport.</summary>
/// <remarks>
/// Combine with <see cref="IRequest{TResponse}"/> for custom result contracts. Queries do not open an
/// automatic transaction by default, but may read within a shared or explicitly requested transaction.
/// They must not perform business writes. This marker does not inspect entities or arbitrary SQL.
/// A request cannot also implement <see cref="ICommandRequest"/>.
/// </remarks>
public interface IQueryRequest;
