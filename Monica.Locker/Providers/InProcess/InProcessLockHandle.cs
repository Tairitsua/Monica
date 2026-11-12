using Monica.Locker.Abstractions;

namespace Monica.Locker.Providers.InProcess;

internal sealed class InProcessLockHandle(IDisposable disposable) : IDistributedLockHandle
{
    // The process-local semaphore has no independent lease that can expire while its handle is held.
    public CancellationToken HandleLostToken => CancellationToken.None;

    public ValueTask DisposeAsync()
    {
        disposable.Dispose();
        return default;
    }
}
