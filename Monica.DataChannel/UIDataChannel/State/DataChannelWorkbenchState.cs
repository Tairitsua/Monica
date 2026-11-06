using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.DataChannel.Facades;
using Monica.DataChannel.UIDataChannel.Models;

namespace Monica.DataChannel.UIDataChannel.State;

/// <summary>Retains detached DataChannel evidence and serializes circuit-local reads and commands.</summary>
/// <remarks>
/// Register as scoped. Routed components borrow this state and own their cancellation, dialogs and pending tasks.
/// This state owns no timer, host pipeline, debugger, component callback or disposable page lifetime.
/// Capture time advances only after a complete successful read. A failed refresh retains the previous evidence.
/// </remarks>
public sealed class DataChannelWorkbenchState(DataChannelFacade facade, ILogger<DataChannelWorkbenchState> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Gets the last complete successful capture, or null before a capture succeeds.</summary>
    public DataChannelManagementSnapshot? Snapshot { get; private set; }

    /// <summary>Gets the most recent refresh error, retained until a refresh succeeds.</summary>
    public string? RefreshError { get; private set; }

    /// <summary>Gets whether an admitted capture is currently being read.</summary>
    public bool IsRefreshing { get; private set; }

    /// <summary>Gets whether a capture has been attempted, including failed or cancelled reads.</summary>
    public bool HasAttemptedCapture { get; private set; }

    /// <summary>Reads all management evidence through one facade capture and adopts it only while the caller is active.</summary>
    /// <param name="cancellationToken">Cancels admission and rejects adoption after the caller retires.</param>
    /// <returns>The complete admitted read, including failure recording; cancellation propagates.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRefreshing = true;
            HasAttemptedCapture = true;
            try
            {
                var result = await facade.GetManagementSnapshotAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.IsFailure())
                {
                    RefreshError = result.Message ?? result.ToString();
                    return;
                }

                Snapshot = result.Data
                    ?? throw new InvalidOperationException("The DataChannel management capture returned no data.");
                RefreshError = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogError(exception, "DataChannel management capture failed");
                RefreshError = exception.Message;
            }
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
        }
    }

    /// <summary>Serializes one actual initialization attempt for the frozen exact channel identity.</summary>
    /// <param name="channelId">The exact registered channel identifier.</param>
    /// <param name="cancellationToken">Cancels admission or the actual endpoint initialization attempt.</param>
    /// <returns>The initialization outcome, independently of a later evidence refresh.</returns>
    public Task<Result> ReinitializeAsync(string channelId, CancellationToken cancellationToken) =>
        ExecuteAsync(() => facade.ReInitializeChannelAsync(channelId, cancellationToken), cancellationToken);

    /// <summary>Clears all retained observable history for one channel while preserving lifetime counters.</summary>
    /// <param name="channelId">The exact identifier frozen before confirmation.</param>
    /// <param name="cancellationToken">Cancels admission and suppresses adoption after retirement.</param>
    /// <returns>The actual clear outcome; clearing does not recover a pipeline or acknowledge a fault.</returns>
    /// <remarks>The underlying synchronous tracker clear cannot be interrupted once admitted.</remarks>
    public Task<Result> ClearHistoryAsync(string channelId, CancellationToken cancellationToken) =>
        ExecuteAsync(() => facade.ClearChannelExceptionsAsync(channelId), cancellationToken);

    private async Task<Result> ExecuteAsync(Func<Task<Result>> command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await command();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { _gate.Release(); }
    }
}
