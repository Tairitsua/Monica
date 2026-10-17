using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.ObjectMapping.Abstractions;
using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;

/// <summary>
/// Provides host-bound infrastructure shared by Monica application and domain services.
/// </summary>
/// <remarks>
/// Instances must be resolved through Monica dependency injection so the cached service provider is assigned
/// before protected host services such as <see cref="Logger"/> or <see cref="Mapper"/> are used. These properties
/// are available after activation and must not be accessed from a derived constructor.
/// </remarks>
public abstract class ServiceBase : ICachedServiceProviderAccessor
{
    private ILogger? _logger;

    /// <inheritdoc />
    public ICachedServiceProvider CachedServiceProvider
    {
        get => field ?? throw CreateNotInitializedException();
        set
        {
            field = value ?? throw new ArgumentNullException(nameof(value));
            _logger = null;
        }
    }

    /// <summary>
    /// Gets a type-specific logger from the host that owns this service instance.
    /// </summary>
    protected ILogger Logger => _logger ??= CachedServiceProvider
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger(GetType());

    /// <summary>
    /// Gets the object mapper from the host that owns this service instance.
    /// </summary>
    protected IObjectMapper Mapper => CachedServiceProvider.GetRequiredService<IObjectMapper>();

    private InvalidOperationException CreateNotInitializedException()
    {
        return new InvalidOperationException(
            $"Cached service provider is not initialized for {GetType().FullName}. Resolve the service through Monica DI instead of constructing it manually.");
    }
}
