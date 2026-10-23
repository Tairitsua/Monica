using Monica.BlobStorage.Abstractions;

namespace Monica.BlobStorage.Extensions;

/// <summary>Convenience operations that retain ownership of their intermediate streams.</summary>
public static class BlobStorageExtensions
{
    /// <summary>Saves bytes without exposing or retaining an intermediate stream.</summary>
    public static async Task SaveAsync(this IBlobStorage storage, string name, byte[] content,
        bool overrideExisting = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(content);
        using var stream = new MemoryStream(content, writable: false);
        await storage.SaveAsync(name, stream, overrideExisting, cancellationToken);
    }

    /// <summary>Reads a complete object and disposes the provider stream; returns null when it is absent.</summary>
    public static async Task<byte[]?> GetAllBytesOrNullAsync(this IBlobStorage storage, string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        await using var stream = await storage.GetOrNullAsync(name, cancellationToken);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
