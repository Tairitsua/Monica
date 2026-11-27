using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.Abstractions;

/// <summary>Provides filesystem inspection and mutation under the current host-wide FileOps policy.</summary>
/// <remarks>The module registers a scoped implementation. Callers borrow it and own their requests and cancellation.
/// Operations throw on policy or filesystem failures; the facade projects those failures into result envelopes.
/// Writes and deletion are not transactional, and cancellation does not undo an applied change.</remarks>
public interface IFileOpsWorkspaceService
{
    /// <summary>Gets a detached, normalized snapshot of the in-memory host policy.</summary>
    Task<FileOpsRuntimeConfig> GetRuntimeConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and replaces the in-memory host policy; it is neither persisted nor user-specific.</summary>
    Task UpdateRuntimeConfigAsync(FileOpsRuntimeConfig runtimeConfig, CancellationToken cancellationToken = default);

    /// <summary>Browses the requested directory, or the safe startup directory when the path is null.</summary>
    /// <remarks>Entries are bounded by the policy limit. A truncated result does not provide a total count.</remarks>
    Task<FileOpsBrowseResult> BrowseAsync(string? path, CancellationToken cancellationToken = default);

    /// <summary>Observes metadata and preview capability for one exact file, even outside the directory display cap.</summary>
    /// <remarks>Resolves the file under current path and hidden-entry policy before observing it.</remarks>
    Task<FileOpsEntrySummary> ObserveFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads bounded decoded text with current metadata and policy capabilities.</summary>
    Task<FileOpsFileTextContent> ReadTextFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Replaces the target text using the requested encoding and current write policy.</summary>
    /// <remarks>No expected-version check, atomic replacement, backup, or rollback is provided.</remarks>
    Task SaveTextFileAsync(FileOpsTextUpdateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates one named child under the captured writable parent and returns its full path.</summary>
    Task<string> CreateDirectoryAsync(FileOpsCreateDirectoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the exact target when policy permits it; allowed roots cannot be deleted.</summary>
    Task<FileOpsDeleteResult> DeleteEntryAsync(FileOpsDeleteRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes distinct targets sequentially using one policy snapshot, stopping on the first failure.</summary>
    /// <remarks>Earlier deletions remain applied after failure; no partial-success result is returned.</remarks>
    Task<FileOpsBatchDeleteResult> DeleteEntriesAsync(FileOpsBatchDeleteRequest request, CancellationToken cancellationToken = default);
}
