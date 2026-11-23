using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Monica.DependencyInjection.Abstractions;

/// <summary>
/// Provides host-bound logging for conventionally activated services.
/// </summary>
/// <remarks>
/// Use a scoped or transient Monica DI convention so activation assigns the cached service provider.
/// Access <see cref="Logger"/> from service methods after activation.
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

    private InvalidOperationException CreateNotInitializedException()
    {
        return new InvalidOperationException(
            $"Cached service provider is not initialized for {GetType().FullName}. Resolve the service through Monica DI instead of constructing it manually.");
    }
}
