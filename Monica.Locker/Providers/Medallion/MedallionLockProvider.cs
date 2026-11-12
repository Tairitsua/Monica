using Medallion.Threading;
using Monica.Locker.Abstractions;
using Monica.Locker.Models;

namespace Monica.Locker.Providers.Medallion;

/// <summary>
/// Adapts a Medallion distributed lock provider to Monica's locker abstraction.
/// </summary>
/// <remarks>
/// Wait timeout and cancellation are forwarded to the native provider. Backend failures propagate, and an acquired
/// handle forwards the native ownership-loss token and asynchronous release. Configure lease expiry and renewal on
/// the native provider; Monica's per-acquisition owner and lease-duration options are not applied by this adapter.
/// </remarks>
public sealed class MedallionLockProvider(
    IDistributedLockProvider distributedLockProvider)
    : ILockProvider
{
    private IDistributedLockProvider DistributedLockProvider { get; } = distributedLockProvider;

    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string normalizedKey,
        LockAcquisitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var waitTimeout = options.WaitTimeout ?? TimeSpan.FromMinutes(2);
        var handle = await DistributedLockProvider.TryAcquireLockAsync(
            normalizedKey,
            waitTimeout,
            cancellationToken
        );

        return handle is null ? null : new MedallionLockHandle(handle);
    }
}
