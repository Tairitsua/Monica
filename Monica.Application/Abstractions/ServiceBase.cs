using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.DependencyInjection.Abstractions;

namespace Monica.Application.Abstractions;

/// <summary>
/// Provides host-bound infrastructure shared by Monica application and domain services.
/// </summary>
/// <remarks>
/// Resolve instances through Monica dependency injection, which assigns the cached service provider after
/// construction. Use <see cref="Logger"/> after activation.
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
