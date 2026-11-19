using Monica.DependencyInjection.Abstractions;

namespace Monica.EventBus.Abstractions.Handlers;

/// <summary>
/// Provides host-bound logging and mapping services to Monica event handlers.
/// </summary>
/// <remarks>
/// Include the concrete handler in type discovery and enable Monica conventional dependency injection
/// to register it as a transient service and initialize its host service provider.
/// </remarks>
public abstract class EventHandlerBase : ServiceBase, ITransientDependency;
