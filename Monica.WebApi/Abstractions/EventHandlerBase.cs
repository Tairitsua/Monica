using Monica.Application.Abstractions;
using Monica.DependencyInjection.Abstractions;

namespace Monica.WebApi.Abstractions;

/// <summary>
/// Provides host-bound logging and mapping services to Monica event handlers.
/// </summary>
/// <remarks>
/// Event handlers must be resolved through Monica dependency injection so their host service provider is assigned.
/// </remarks>
public abstract class EventHandlerBase : ServiceBase, ITransientDependency;
