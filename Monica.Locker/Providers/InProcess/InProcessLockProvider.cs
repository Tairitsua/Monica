using System.Runtime.CompilerServices;
using AsyncKeyedLock;
using Monica.Locker.Abstractions;
using Monica.Locker.Models;

namespace Monica.Locker.Providers.InProcess;

/// <summary>
/// Provides process-local locking backed by <c>AsyncKeyedLock</c>.
/// </summary>
public sealed class InProcessLockProvider : ILockProvider
{
    /// <inheritdoc />
    public LockProviderInfo Info => new(LockProviderKind.InProcess);

    // Reuse keyed semaphore instances across acquisitions to avoid allocating a new lock object per key.
    private readonly AsyncKeyedLocker<string> _lockPool = new(options =>
    {
        options.PoolSize = 20;
        options.PoolInitialFill = 1;
    });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string normalizedKey,
        LockAcquisitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var waitTimeout = options.WaitTimeout ?? TimeSpan.FromMinutes(2);
        var releaser = await _lockPool.LockOrNullAsync(normalizedKey, waitTimeout, cancellationToken);
        return releaser is null ? null : new InProcessLockHandle(releaser);
    }
}
