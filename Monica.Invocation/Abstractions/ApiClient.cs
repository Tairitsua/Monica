using Monica.DependencyInjection.Abstractions;

namespace Monica.Invocation.Abstractions;

/// <summary>Base type for explicitly registered API clients that use the current host's cached services.</summary>
/// <param name="serviceProvider">The borrowed service provider for the client's dependency-injection scope.</param>
/// <remarks>Local and HTTP adapters derive from this type. Application-model clients instead implement their API and delegate to IApplicationApiInvoker.</remarks>
public abstract class ApiClient(ICachedServiceProvider serviceProvider) : IApplicationApi
{
    /// <summary>Gets the borrowed service provider. Its owning scope must outlive each invocation.</summary>
    protected ICachedServiceProvider CachedServiceProvider { get; } = serviceProvider;
}
