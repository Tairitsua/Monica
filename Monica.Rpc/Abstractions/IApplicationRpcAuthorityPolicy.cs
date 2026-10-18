using Monica.Rpc.Models;

namespace Monica.Rpc.Abstractions;

/// <summary>Applies product authority rules before ordinary Local or required-role routing.</summary>
/// <remarks>
/// Register one scoped policy through UseAuthorityPolicy. A nonnull destination forces remote delivery, including
/// a different deployment scope with the same logical AppId. Null preserves normal role placement. Policies must
/// not execute the operation, retry it, or trust client-supplied identity or destination metadata.
/// </remarks>
public interface IApplicationRpcAuthorityPolicy
{
    /// <summary>Returns the preconfigured authority destination, or null to use normal placement.</summary>
    ValueTask<ApplicationRpcDestination?> ResolveAsync(ApplicationRpcAuthorityContext context,
        CancellationToken cancellationToken = default);
}
