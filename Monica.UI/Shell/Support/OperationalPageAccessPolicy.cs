using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.UI.Shell.Support;

/// <summary>
/// Defines the optional authorization-policy override exposed by one Monica operational UI module.
/// </summary>
public interface IOperationalPageAccessOptions
{
    /// <summary>
    /// Gets the host authorization policy that overrides the shell-wide operational policy for this page.
    /// A missing or blank value inherits <see cref="OperationalPageAccessOption.AuthorizationPolicy"/>.
    /// </summary>
    string? AuthorizationPolicyOverride { get; }
}

/// <summary>
/// Evaluates the shell-wide access contract for Monica operational pages and protected diagnostics surfaces.
/// </summary>
/// <remarks>
/// The evaluator is scoped to the current Blazor circuit so it can safely read circuit-owned authentication state.
/// It deliberately fails closed when an effective policy or required authorization service cannot be resolved.
/// </remarks>
public sealed class OperationalPageAccessEvaluator(
    IHostEnvironment environment,
    IOptions<ModuleShellUIOption> shellOptions,
    IServiceProvider services)
{
    /// <summary>
    /// Determines whether the current circuit may access an operational surface.
    /// </summary>
    /// <param name="authorizationPolicyOverride">
    /// An optional page-owned policy name. Blank values inherit the shell-wide policy.
    /// </param>
    /// <param name="cancellationToken">Cancels the current access evaluation.</param>
    /// <returns><see langword="true"/> when access is allowed; otherwise <see langword="false"/>.</returns>
    public async Task<bool> IsAuthorizedAsync(
        string? authorizationPolicyOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var accessOptions = shellOptions.Value.OperationalPageAccess;
        if (accessOptions.BypassAuthorization || environment.IsDevelopment())
        {
            return true;
        }

        var policyName = accessOptions.ResolveAuthorizationPolicy(authorizationPolicyOverride);
        if (policyName is null)
        {
            return false;
        }

        var authenticationStateProvider = services.GetService<AuthenticationStateProvider>();
        var authorizationService = services.GetService<IAuthorizationService>();
        var policyProvider = services.GetService<IAuthorizationPolicyProvider>();
        if (authenticationStateProvider is null || authorizationService is null || policyProvider is null)
        {
            return false;
        }

        try
        {
            var policy = await policyProvider.GetPolicyAsync(policyName);
            cancellationToken.ThrowIfCancellationRequested();
            if (policy is null)
            {
                return false;
            }

            var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var authorization = await authorizationService.AuthorizeAsync(
                authenticationState.User,
                resource: null,
                policy.Requirements);
            cancellationToken.ThrowIfCancellationRequested();
            return authorization.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Adapts one operational UI module's optional policy override to the shared shell access evaluator.
/// </summary>
/// <typeparam name="TOptions">The operational UI module's option type.</typeparam>
public sealed class OperationalPageAccessPolicy<TOptions>(
    OperationalPageAccessEvaluator evaluator,
    IOptions<TOptions> options) : IPageAccessPolicy
    where TOptions : class, IOperationalPageAccessOptions
{
    /// <inheritdoc />
    public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default) =>
        evaluator.IsAuthorizedAsync(options.Value.AuthorizationPolicyOverride, cancellationToken);
}
