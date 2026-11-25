namespace Monica.Core.ApplicationRoles;

/// <summary>Tracks in-flight work for one locally owned resource so quarantine can close admission and await settlement.</summary>
/// <remarks>Create one gate per owner or resource and close it permanently on quarantine. An admission belongs to its caller.</remarks>
public sealed class RoleWorkAdmission
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _closed;
    private int _active;

    /// <summary>Admits one operation or returns null after quarantine. Dispose the returned lease after settlement.</summary>
    public Lease? TryEnter()
    {
        lock (_gate)
        {
            if (_closed) return null;
            _active++;
            return new Lease(this);
        }
    }

    /// <summary>Permanently closes admission and completes when all previously admitted work has settled.</summary>
    public Task CloseAndDrainAsync()
    {
        lock (_gate)
        {
            _closed = true;
            if (_active == 0) _drained.TrySetResult();
            return _drained.Task;
        }
    }

    /// <summary>Owns one outstanding operation, including work already accepted by a durable claim.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly RoleWorkAdmission _owner;
        private bool _released;
        internal Lease(RoleWorkAdmission owner) => _owner = owner;

        /// <summary>Retains already-admitted work past this lease's lifetime, for example when a claim yields an execution.</summary>
        public Lease Retain()
        {
            lock (_owner._gate)
            {
                ObjectDisposedException.ThrowIf(_released, this);
                _owner._active++;
                return new Lease(_owner);
            }
        }

        /// <summary>Releases this operation without cancelling it or other admissions.</summary>
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_released) return;
                _released = true;
                if (--_owner._active == 0 && _owner._closed) _owner._drained.TrySetResult();
            }
        }
    }
}
