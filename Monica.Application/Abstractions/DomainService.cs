using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;

/// <summary>
/// Identifies a domain service building block and provides host-bound services to it.
/// </summary>
/// <remarks>
/// Derive from this base to declare a domain service. Monica DI conventionally registers concrete domain services
/// as transient services.
/// </remarks>
public abstract class DomainService : ServiceBase, ITransientDependency;
