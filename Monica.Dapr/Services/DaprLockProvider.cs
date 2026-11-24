using Dapr.DistributedLock;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Locker.Abstractions;
using Monica.Locker.Models;
using Monica.Modules;

namespace Monica.Dapr.Services;

#pragma warning disable DAPR_DISTRIBUTEDLOCK // DaprDistributedLockClient is evaluation API
/// <summary>
/// Acquires distributed locks through Dapr's lock API.
/// </summary>
public sealed class DaprLockProvider(
    DaprDistributedLockClient client,
    IOptions<ModuleDaprLockerOption> distributedLockDaprOptions,
    ILogger<DaprLockProvider> logger)
    : ILockProvider
#pragma warning restore DAPR_DISTRIBUTEDLOCK
{
    /// <inheritdoc />
    public LockProviderInfo Info => new(LockProviderKind.Dapr, DistributedLockDaprOptions.StoreName, DistributedLockDaprOptions.DefaultLeaseDuration);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private ModuleDaprLockerOption DistributedLockDaprOptions { get; } = distributedLockDaprOptions.Value;

    public async Task<IDistributedLockHandle?> TryAcquireAsync(
        string normalizedKey,
        LockAcquisitionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Wait timeout controls how long we keep polling for the lock. Lease duration controls how long Dapr holds the
        // lock after acquisition. They are intentionally separate because callers often wait briefly for a longer lease.
        var waitTimeout = options.WaitTimeout ?? DistributedLockDaprOptions.DefaultLeaseDuration;
        var leaseDuration = options.LeaseDuration ?? DistributedLockDaprOptions.DefaultLeaseDuration;
        var owner = !string.IsNullOrWhiteSpace(options.Owner)
            ? options.Owner
            : $"{DistributedLockDaprOptions.LockOwnerPrefix}{Guid.NewGuid():N}";

        // Combine caller cancellation with the acquisition timeout so the polling loop exits cleanly for either reason.
        using var waitTimeoutSource = new CancellationTokenSource(waitTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            waitTimeoutSource.Token);
        var token = linkedSource.Token;

        while (true)
        {
            try
            {
                var lockResponse = await client.TryLockAsync(
                    DistributedLockDaprOptions.StoreName,
                    normalizedKey,
                    owner,
                    Math.Max(1, (int)Math.Ceiling(leaseDuration.TotalSeconds)),
                    token);

                if (lockResponse != null)
                {
                    return new DaprLockHandle(lockResponse);
                }

                // Dapr returns null when another owner still holds the lock, so we retry until timeout/cancellation.
                await Task.Delay(RetryDelay, token);
            }
            catch (Exception ex) when (IsTransientLockException(ex))
            {
                logger.LogWarning(ex, "Transient failure while acquiring Dapr distributed lock {LockKey}.", normalizedKey);
                if (!await DelayBeforeRetryAsync(token))
                {
                    return null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Timed out acquiring Dapr distributed lock {LockKey}.", normalizedKey);
                return null;
            }
        }
    }

    private static async Task<bool> DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RetryDelay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsTransientLockException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("i/o timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("deadline", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("temporarily", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var typeName = current.GetType().Name;
            if (typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("RpcException", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
