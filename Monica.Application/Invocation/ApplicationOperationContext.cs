namespace Monica.Application.Invocation;

/// <summary>Tracks an active operation across awaited nested calls without sharing scopes between independent callers.</summary>
internal sealed class ApplicationOperationContext
{
    private readonly AsyncLocal<Operation?> _current = new();

    public IServiceProvider? Services => _current.Value switch
    {
        { Disposed: true } => throw new InvalidOperationException("The application operation has completed. Await nested calls before returning."),
        { } operation => operation.Services,
        _ => null
    };

    public IDisposable Enter(IServiceProvider services)
    {
        var parent = _current.Value;
        var operation = new Operation(services);
        _current.Value = operation;
        return new Lease(() => { operation.Disposed = true; _current.Value = parent; });
    }

    private sealed class Operation(IServiceProvider services)
    {
        public IServiceProvider Services { get; } = services;
        public bool Disposed { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
