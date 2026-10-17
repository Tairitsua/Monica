using System.Security.Claims;

namespace Monica.Authority.Identity.Services;

/// <summary>Overrides ambient HTTP or system identity for a trusted adapter's asynchronous operation.</summary>
/// <remarks>Registered once per host. Values flow only with the current execution context. This is not authentication.</remarks>
public sealed class CurrentPrincipalContext
{
    private readonly AsyncLocal<ClaimsPrincipal?> _current = new();

    /// <summary>The explicit caller, including an anonymous principal, or null outside an adapter-owned operation.</summary>
    public ClaimsPrincipal? Principal => _current.Value;

    /// <summary>Installs a snapshot of a trusted caller. Dispose in reverse order after the operation completes.</summary>
    /// <remarks>Never populate this context from request payload claims. An explicit anonymous principal suppresses system fallback.</remarks>
    public IDisposable Use(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var previous = _current.Value;
        _current.Value = principal.Clone();
        return new RestoreScope(this, previous);
    }

    private sealed class RestoreScope(CurrentPrincipalContext owner, ClaimsPrincipal? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
