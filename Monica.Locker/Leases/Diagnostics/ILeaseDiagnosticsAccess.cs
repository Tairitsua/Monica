namespace Monica.Locker.Leases.Diagnostics;

/// <summary>Authorizes the current caller before any diagnostic query or administrative mutation.</summary>
/// <remarks>Scoped to the caller. The default denies access; the UI supplies its operational-policy evaluator.</remarks>
public interface ILeaseDiagnosticsAccess
{
    /// <summary>Evaluates current access; implementors must not cache a user's earlier authorization indefinitely.</summary>
    Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default);
}

internal sealed class DenyLeaseDiagnosticsAccess : ILeaseDiagnosticsAccess
{
    public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
