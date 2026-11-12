using Monica.DependencyInjection.Abstractions;
using Monica.Invocation.Models;
using Monica.Invocation.Services;

namespace Monica.Invocation.Abstractions;

/// <summary>Base type for explicitly registered API clients that use the current host's cached services.</summary>
/// <param name="serviceProvider">The borrowed service provider for the client's dependency-injection scope.</param>
/// <remarks>Local and HTTP adapters derive from this type. Application-model clients instead implement their API and delegate to IApplicationApiInvoker.</remarks>
public abstract class ApiClient(ICachedServiceProvider serviceProvider) : IApplicationApi
{
    /// <summary>Gets the borrowed service provider. Its owning scope must outlive each invocation.</summary>
    protected ICachedServiceProvider CachedServiceProvider { get; } = serviceProvider;

    /// <summary>Gets this immutable call view's participation policy. Defaults to Shared.</summary>
    protected ApiTransaction Transaction { get; private set; } = ApiTransaction.Shared;

    /// <summary>Creates a typed call view without changing this client or allocating execution resources.</summary>
    /// <remarks>The view borrows this client's dependencies and preserves derived overrides; it owns no service scope.</remarks>
    protected TApi WithTransactionCore<TApi>(ApiTransaction transaction) where TApi : IApplicationApi
    {
        ApiTransactionPolicy.Validate(transaction);
        var view = (ApiClient)MemberwiseClone();
        view.Transaction = transaction;
        return (TApi)(object)view;
    }
}
