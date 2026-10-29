namespace Monica.Repository.Snapshots.Models;

/// <summary>A diagnostic view of one retained projection. It does not revalidate the database.</summary>
public sealed record RepositorySnapshotEntryStatus(SnapshotVersion Version,
    SnapshotProjectionIdentity ProjectionIdentity, DateTimeOffset ValidatedAtUtc, TimeSpan ValidationAge);

/// <summary>Host-local capacity and provenance evidence captured at one instant.</summary>
public sealed record RepositorySnapshotStatus(string SourceId, int MaximumCachedEntries,
    int PendingBuilds, int MaximumPendingBuilds, IReadOnlyList<RepositorySnapshotEntryStatus> Entries);
