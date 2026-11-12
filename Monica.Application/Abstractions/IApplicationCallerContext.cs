using System.Security.Claims;

namespace Monica.Application.Abstractions;

/// <summary>Captures the trusted caller at the outer Local operation boundary.</summary>
/// <remarks>Scoped to the caller, never the newly created operation. Implementations must support concurrent calls.</remarks>
public interface IApplicationCallerContext
{
    /// <summary>Reads the current caller. Client-supplied identity and tenant claims must not be trusted directly.</summary>
    ValueTask<ClaimsPrincipal> GetPrincipalAsync(CancellationToken cancellationToken);
}
