using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.ObjectMapping.Abstractions;

namespace Monica.DependencyInjection.Abstractions;

/// <summary>
/// Provides host-bound logging and object mapping for conventionally activated services.
/// </summary>
/// <remarks>
/// Use a scoped or transient Monica DI convention so activation assigns the cached service provider.
/// Access <see cref="Logger"/> and <see cref="Mapper"/> from service methods after activation.
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
