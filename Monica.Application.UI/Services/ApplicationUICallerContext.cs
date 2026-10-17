using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Monica.Application.Abstractions;
using Monica.Authority.Identity.Abstractions;

namespace Monica.Application.UI.Services;

internal sealed class ApplicationUICallerContext(
    ApplicationCircuitState circuit,
    AuthenticationStateProvider authentication,
    ICurrentPrincipalAccessor principal) : IApplicationCallerContext
{
    public async ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Prerender and HTTP/job callers use their native trusted identity. A circuit reads its live auth state.
        return circuit.IsCircuit
            ? (await authentication.GetAuthenticationStateAsync().WaitAsync(cancellationToken)).User
            : principal.Principal;
    }
}
