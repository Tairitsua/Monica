using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;
/// <summary>
/// Dependency-injection marker carried by the <see cref="DomainService"/> base class.
/// </summary>
/// <remarks>
/// Building-block recognition for domain services requires deriving from <see cref="DomainService"/>.
/// Implementing this interface directly registers the class for conventional dependency injection only;
/// such a class stays outside the building-block catalog and source analysis.
/// </remarks>
public interface IDomainService : ITransientDependency
{

}

/// <summary>
/// Identifies a domain service building block and provides host-bound services to it.
/// </summary>
public abstract class DomainService : ServiceBase, IDomainService
{
}
