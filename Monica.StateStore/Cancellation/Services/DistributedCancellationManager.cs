using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Modules;
using Monica.StateStore.Abstractions;
using Monica.StateStore.Cancellation.Abstractions;
using Monica.Tool.Extensions;

// ReSharper disable PossiblyMistakenUseOfCancellationToken

namespace Monica.StateStore.Cancellation.Services;

//Should TODO use distributed locks?
//TODO Use EventBus instead of polling
//TODO replace InMemoryCancellationManager

/// <summary>
/// Default distributed cancellation token manager implementation
/// Use IStateStore as the underlying storage to implement cancellation token management across microservice instances
/// </summary>
/// <remarks>
/// Initialize the default distributed cancellation token manager
/// </remarks>
/// <param name="stateStore">State storage service</param>
/// <param name="logger">Logger</param>
/// <param name="options">Configuration options</param>
public class DistributedCancellationManager(
    IStateStore stateStore,
    ILogger<DistributedCancellationManager> logger,
    IOptions<ModuleCancellationManagerOption> options) : ICancellationManager
{
    private const string STATE_KEY_PREFIX = "DistributedCancellation:";

    private readonly ModuleCancellationManagerOption _options = options.Value;

    /// <summary>
    /// Locally cancel token source cache to avoid repeated polling of state storage
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _localTokenSources = new();

    /// <summary>
    /// Polling ownership markers; each key has at most one background observer.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _pollingTasks = new();

    /// <summary>
    /// Get prefixed key for storage
    /// </summary>
    private static string GetPrefixedKey(string key) => STATE_KEY_PREFIX + key;

    /// <summary>
    /// Create or obtain a distributed cancellation token for the specified key
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Returns the cancellation token associated with the specified key</returns>
    public async Task<CancellationToken> GetOrCreateTokenAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Token key cannot be null or empty", nameof(key));

        // If the local already exists and has not been canceled, return directly.
        if (_localTokenSources.TryGetValue(key, out var existingSource) && !existingSource.Token.IsCancellationRequested)
        {
            if (_options.EnableVerboseLogging)
                logger.LogDebug("Returning existing local cancellation token for key: {Key}", key);
            return existingSource.Token;
        }

        // Create a new local cancellation token source
        var tokenSource = new CancellationTokenSource();
        _localTokenSources.AddOrUpdate(key, tokenSource, (_, _) => tokenSource);

        // Initialize or obtain distributed state
        var state = await stateStore.GetStateAsync<DistributedCancellationTokenState>(GetPrefixedKey(key), cancellationToken);
        if (state == null)
        {
            // Create new distributed state
            state = new DistributedCancellationTokenState
            {
                Key = key,
                IsCancelled = false,
                CreatedAt = DateTime.Now,
                LastUpdatedAt = DateTime.Now,
                Version = 1
            };

            await stateStore.SaveStateAsync(GetPrefixedKey(key), state, cancellationToken, _options.StateTtl);
            logger.LogInformation("Created new distributed cancellation token for key: {Key}", key);
        }

        // If the distributed state has been canceled, immediately cancel the local token
        if (state.IsCancelled)
        {
            await tokenSource.CancelAsync();
            if (_options.EnableVerboseLogging)
                logger.LogDebug("Local token immediately cancelled due to distributed state for key: {Key}", key);
        }

        // Start a background polling task to monitor distributed status changes
        StartPollingTask(key);

        return tokenSource.Token;
    }

    /// <summary>
    /// Cancels a distributed cancellation token for a specified key
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    public async Task CancelTokenAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Token key cannot be null or empty", nameof(key));

        try
        {
            // Update distributed status
            var state = await stateStore.GetStateAsync<DistributedCancellationTokenState>(GetPrefixedKey(key), cancellationToken);
            if (state != null)
            {
                state.IsCancelled = true;
                state.LastUpdatedAt = DateTime.Now;
                state.Version++;

                await stateStore.SaveStateAsync(GetPrefixedKey(key), state, cancellationToken, _options.StateTtl);
                logger.LogInformation("Cancelled distributed cancellation token for key: {Key}", key);
            }

            // Cancel local token
            if (_localTokenSources.TryGetValue(key, out var tokenSource))
            {
                await tokenSource.CancelAsync();
            }
        }
        catch (Exception ex)
        {
            throw ex.CreateException(logger, "Failed to cancel distributed cancellation token for key: {0}", key);
        }
    }

    /// <summary>
    /// Checks whether the cancellation token for the specified key has been canceled
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Returns true if canceled, false otherwise</returns>
    public async Task<bool> IsCancelledAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Token key cannot be null or empty", nameof(key));

        var state = await stateStore.GetStateAsync<DistributedCancellationTokenState>(GetPrefixedKey(key), cancellationToken);
        return state?.IsCancelled ?? false;
    }

    /// <summary>
    /// Resets the cancellation token status of the specified key
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    public async Task ResetTokenAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Token key cannot be null or empty", nameof(key));

        try
        {
            // Reset distributed state
            var state = await stateStore.GetStateAsync<DistributedCancellationTokenState>(GetPrefixedKey(key), cancellationToken)
                ?? new DistributedCancellationTokenState { Key = key, Version = 0 };
            state.IsCancelled = false;
            state.LastUpdatedAt = DateTime.Now;
            state.Version++;

            // Persist even for a new key before polling starts; missing state means deletion to observers.
            await stateStore.SaveStateAsync(GetPrefixedKey(key), state, cancellationToken, _options.StateTtl);

            // Reset local token source
            var newTokenSource = new CancellationTokenSource();
            _localTokenSources.AddOrUpdate(key, newTokenSource, (_, _) => newTokenSource);

            // Restart polling task
            StartPollingTask(key);

            logger.LogInformation("Reset distributed cancellation token for key: {Key}", key);
        }
        catch (Exception ex)
        {
            throw ex.CreateException(logger, "Failed to reset distributed cancellation token for key: {0}", key);
        }
    }

    /// <summary>
    /// Removes the cancellation token for the specified key
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    public async Task DeleteTokenAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Token key cannot be null or empty", nameof(key));

        try
        {
            // Delete distributed state
            await stateStore.DeleteStateAsync(GetPrefixedKey(key), cancellationToken);

            // Clean up local resources
            if (_localTokenSources.TryRemove(key, out var tokenSource))
            {
                await tokenSource.SafeCancelAndDisposeAsync();
            }

            logger.LogInformation("Deleted distributed cancellation token for key: {Key}", key);
        }
        catch (Exception ex)
        {
            throw ex.CreateException(logger, "Failed to delete distributed cancellation token for key: {0}", key);
        }
    }

    /// <summary>
    /// Get a list of keys for all active cancellation tokens
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Returns a list of keys for all active cancellation tokens</returns>
    public async Task<IReadOnlyList<string>> GetActiveTokenKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Since StateStore's QueryBuilder does not support universal prefix queries, we need to obtain all states through other means
            // A simplified implementation is used here. In actual use, it may need to be adjusted according to the specific IStateStore implementation.
            var result = new List<string>();

            // This is a stop-gap measure and should be implemented in a more efficient way in actual production environments.
            // For example: maintain an index of active token keys, or use a storage implementation that supports prefix queries
            logger.LogWarning("GetActiveTokenKeysAsync is using a simplified implementation. " +
                             "Consider implementing a more efficient solution for production use.");

            // Returns the active token key currently in the local cache
            foreach (var kvp in _localTokenSources)
            {
                if (!kvp.Value.Token.IsCancellationRequested)
                {
                    // Double checking distributed state
                    var state = await stateStore.GetStateAsync<DistributedCancellationTokenState>(GetPrefixedKey(kvp.Key), cancellationToken);
                    if (state != null && !state.IsCancelled)
                    {
                        result.Add(kvp.Key);
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            throw ex.CreateException(logger, "Failed to get active token keys");
        }
    }

    /// <summary>
    /// Cancel multiple cancellation tokens in batches
    /// </summary>
    /// <param name="keys">List of cancellation token keys to cancel</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    public async Task CancelTokensAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default)
    {
        if (keys == null || !keys.Any())
            return;

        var tasks = keys.Select(key => CancelTokenAsync(key, cancellationToken));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Start a background polling task to monitor distributed status changes
    /// </summary>
    /// <param name="key">cancel token key</param>
    private void StartPollingTask(string key)
    {
        // Claim ownership before scheduling, including when the worker finishes synchronously.
        if (!_pollingTasks.TryAdd(key, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var pollingIntervalMs = _options.PollingIntervalMs;
            var lastVersion = 0L;
            _localTokenSources.TryGetValue(key, out var observedSource);

            if (_options.EnableVerboseLogging)
                logger.LogDebug("Started polling task for cancellation token key: {Key} with interval {IntervalMs}ms", key, pollingIntervalMs);

            while (_localTokenSources.TryGetValue(key, out var tokenSource)
                   && ReferenceEquals(tokenSource, observedSource) && !tokenSource.IsCancellationRequested)
            {
                try
                {
                    var state = await stateStore.GetStateAsync<DistributedCancellationTokenState>(GetPrefixedKey(key));
                    if (state == null)
                    {
                        // The distributed state has been deleted and local resources have been cleaned up.
                        // A late read from an old observer must not remove a replacement created by reset.
                        if (_localTokenSources.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, tokenSource)))
                        {
                            await tokenSource.SafeCancelAndDisposeAsync();
                        }
                        if (_options.EnableVerboseLogging)
                            logger.LogDebug("Distributed state deleted, cleaned up local resources for key: {Key}", key);
                        break;
                    }

                    // Check version changes and cancellation status
                    if (state.Version > lastVersion)
                    {
                        lastVersion = state.Version;

                        if (state.IsCancelled && !tokenSource.Token.IsCancellationRequested)
                        {
                            await tokenSource.CancelAsync();
                            logger.LogDebug("Local cancellation token cancelled due to distributed state change for key: {Key}", key);
                            break;
                        }
                    }

                    await Task.Delay(pollingIntervalMs, tokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // Cancel normally and exit polling
                    if (_options.EnableVerboseLogging)
                        logger.LogDebug("Polling task cancelled for key: {Key}", key);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Error occurred during polling for key: {Key}", key);
                    await Task.Delay(pollingIntervalMs);
                }
            }

            // A reset may have replaced a cancelled source while this worker was exiting.
            _pollingTasks.TryRemove(key, out _);
            if (_options.EnableVerboseLogging)
            {
                logger.LogDebug("Polling task completed for key: {Key}", key);
            }
            if (_localTokenSources.TryGetValue(key, out var currentSource) && !currentSource.IsCancellationRequested)
            {
                StartPollingTask(key);
            }
        });
    }

    /// <summary>
    /// Distributed Cancellation Token State Data Model
    /// </summary>
    private class DistributedCancellationTokenState
    {
        /// <summary>
        /// cancel token key
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Has it been cancelled?
        /// </summary>
        public bool IsCancelled { get; set; }

        /// <summary>
        /// creation time
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Last updated
        /// </summary>
        public DateTime LastUpdatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Version number, used for optimistic lock control
        /// </summary>
        public long Version { get; set; } = 1;
    }
}
