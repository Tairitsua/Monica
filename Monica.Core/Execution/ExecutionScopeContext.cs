namespace Monica.Core.Execution;

/// <summary>Tracks the borrowed dependency scope of an awaited execution within one host.</summary>
/// <remarks>
/// Registered as a host-owned singleton. Entries restore their parent on disposal; independent operations enter
/// a fresh scope before resolving collaborators. This context does not own or dispose the service provider.
/// </remarks>
public sealed class ExecutionScopeContext
{
    private readonly AsyncLocal<Scope?> _current = new();

    /// <summary>Gets the current scope, or null outside execution. An escaped, completed execution is rejected.</summary>
    public IServiceProvider? Services => _current.Value switch
    {
        { Disposed: true } => throw new InvalidOperationException("The execution scope has completed. Await nested calls before returning."),
        { } scope => scope.Services,
        _ => null
    };

    /// <summary>Enters a borrowed scope until the returned lease is disposed, restoring its parent afterward.</summary>
    /// <remarks>Dispose in nesting order and await all child work before releasing the lease.</remarks>
    public IDisposable Enter(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var parent = _current.Value;
        var scope = new Scope(services);
        _current.Value = scope;
        return new Lease(() => { scope.Disposed = true; _current.Value = parent; });
    }

    private sealed class Scope(IServiceProvider services)
    {
        public IServiceProvider Services { get; } = services;
        public bool Disposed { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
