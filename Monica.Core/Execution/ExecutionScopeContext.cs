using Microsoft.Extensions.DependencyInjection;

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
    private Scope? Current => _current.Value switch
    {
        { Disposed: true } => throw new InvalidOperationException("The execution scope has completed. Await nested calls before returning."),
        { } scope => scope,
        _ => null
    };

    /// <summary>Gets the borrowed service scope, or null outside execution.</summary>
    public IServiceProvider? Services => Current?.Services;

    /// <summary>Gets the current operation's immutable metadata, or null outside a described execution.</summary>
    public ExecutionDescriptor? Operation => Current?.Operation;

    /// <summary>Gets the current execution identity, or null outside a described execution.</summary>
    public Guid? InvocationId => Current?.InvocationId;

    /// <summary>Gets the dependency scope of the active logical unit of work, or null when none has started.</summary>
    /// <param name="fallbackServices">The caller-owned scope to inspect when no execution scope is entered.</param>
    /// <remarks>
    /// An execution scope alone does not establish a unit of work. An active unit of work is shared even before it
    /// opens a physical transaction or after it becomes rollback-only. This method never creates or resets a scope.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The execution or its unit of work has already ended.</exception>
    public IServiceProvider? GetActiveUnitOfWorkServices(IServiceProvider? fallbackServices = null)
    {
        var services = Services ?? fallbackServices;
        return services?.GetService<IExecutionTransaction>() is { IsOperationActive: true } ? services : null;
    }

    /// <summary>Enters a borrowed scope until the returned lease is disposed, restoring its parent afterward.</summary>
    /// <remarks>
    /// Dispose in nesting order and await all child work before releasing the lease. When operation metadata is
    /// omitted it is inherited only within the same provider; independent scopes do not inherit their caller's operation.
    /// </remarks>
    public IDisposable Enter(IServiceProvider services, ExecutionDescriptor? operation = null, Guid? invocationId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var parent = _current.Value;
        var inherit = operation is null && parent is { Disposed: false } && ReferenceEquals(parent.Services, services);
        var scope = new Scope(services, inherit ? parent!.Operation : operation, inherit ? parent!.InvocationId : invocationId);
        _current.Value = scope;
        return new Lease(() => { scope.Disposed = true; _current.Value = parent; });
    }

    private sealed class Scope(IServiceProvider services, ExecutionDescriptor? operation, Guid? invocationId)
    {
        public IServiceProvider Services { get; } = services;
        public ExecutionDescriptor? Operation { get; } = operation;
        public Guid? InvocationId { get; } = invocationId;
        public bool Disposed { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
