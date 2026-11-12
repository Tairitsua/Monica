using Medallion.Threading;
using Monica.Locker.Abstractions;

namespace Monica.Locker.Providers.Medallion;

internal sealed class MedallionLockHandle(IDistributedSynchronizationHandle handle) : IDistributedLockHandle
{
    public CancellationToken HandleLostToken => handle.HandleLostToken;

    public ValueTask DisposeAsync()
    {
        return handle.DisposeAsync();
    }
}
