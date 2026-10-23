namespace Monica.BlobStorage.Abstractions;

/// <summary>Stores named binary objects in the provider's configured container.</summary>
/// <remarks>
/// Names are opaque provider-relative keys. The host owns the provider and its connections; callers own input
/// streams and must dispose returned streams. Implementations must be safe for concurrent use.
/// </remarks>
public interface IBlobStorage
{
    /// <summary>Saves the readable stream from its current position without disposing it.</summary>
    /// <param name="name">Nonempty logical object name.</param>
    /// <param name="content">Caller-owned readable stream; the operation advances its position.</param>
    /// <param name="overrideExisting">False by default; rejects an existing object. Atomicity is provider-specific.</param>
    /// <param name="cancellationToken">Cancels supported provider operations.</param>
    Task SaveAsync(string name, Stream content, bool overrideExisting = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an object, returning false when the object or container does not exist.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Returns whether the object exists; a missing container returns false.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Reads an object, or returns null when the object or container does not exist.</summary>
    /// <returns>A readable stream positioned at its beginning and owned by the caller, or null.</returns>
    /// <remarks>Providers may buffer the complete object. Authorization and transport failures are not absence.</remarks>
    Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default);
}
