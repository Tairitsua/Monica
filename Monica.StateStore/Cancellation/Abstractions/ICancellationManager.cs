namespace Monica.StateStore.Cancellation.Abstractions;

/// <summary>
/// (Singleton) Distributed cancellation token manager interface
/// Provide cancellation token creation, cancellation and monitoring functions across microservice instances
/// </summary>
public interface ICancellationManager
{
    /// <summary>
    /// Create or obtain a distributed cancellation token for the specified key
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Returns the cancellation token associated with the specified key</returns>
    Task<CancellationToken> GetOrCreateTokenAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a distributed cancellation token for a specified key
    /// This will trigger the cancellation token in all microservice instances listening for this key
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    Task CancelTokenAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the cancellation token for the specified key has been canceled
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Returns true if canceled, false otherwise</returns>
    Task<bool> IsCancelledAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the cancellation token status of the specified key
    /// Establishes an active token when the key is missing, or resets its cancelled state for reuse.
    /// A distributed implementation persists that active state before starting observation.
    /// Obtain the new token with <see cref="GetOrCreateTokenAsync"/>; previously returned tokens are not reactivated.
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    Task ResetTokenAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the cancellation token for the specified key
    /// Clean up cancellation token resources that are no longer needed
    /// </summary>
    /// <param name="key">Unique identification key for cancellation token</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    Task DeleteTokenAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a list of keys for all active cancellation tokens
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    /// <returns>Returns a list of keys for all active cancellation tokens</returns>
    Task<IReadOnlyList<string>> GetActiveTokenKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel multiple cancellation tokens in batches
    /// </summary>
    /// <param name="keys">List of cancellation token keys to cancel</param>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    Task CancelTokensAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken = default);
} 