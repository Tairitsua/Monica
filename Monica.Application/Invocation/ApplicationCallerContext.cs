using System.Security.Claims;
using Monica.Application.Abstractions;
using Monica.Authority.Identity.Abstractions;

namespace Monica.Application.Invocation;

internal sealed class ApplicationCallerContext(ICurrentPrincipalAccessor principal) : IApplicationCallerContext
{
    public ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(principal.Principal);
    }
}
