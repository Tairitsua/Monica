using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;
/// <summary>
/// This interface can be implemented by all domain services to identify them by convention.
/// </summary>
public interface IDomainService : ITransientDependency
{

}

public abstract class DomainService : ServiceBase, IDomainService
{
}
