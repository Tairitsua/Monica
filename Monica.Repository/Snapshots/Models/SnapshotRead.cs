namespace Monica.Repository.Snapshots.Models;

/// <summary>The authoritative identity of one committed source dataset.</summary>
public sealed record SnapshotVersion(string SourceId, string DatasetKey, Guid Epoch, long Revision);

/// <summary>Identifies a derived result independently of its source dataset revision.</summary>
public sealed record SnapshotProjectionIdentity(string Name, int AlgorithmVersion, int FormatVersion, string CanonicalInputs);

/// <summary>An immutable projection and the evidence supporting this read. Age uses a monotonic clock.</summary>
public sealed record SnapshotRead<T>(T Value, SnapshotVersion Version,
    SnapshotProjectionIdentity ProjectionIdentity, DateTimeOffset ValidatedAtUtc,
    TimeSpan ValidationAge, bool IsDegraded) where T : class;

/// <summary>Persistent dataset authority. Initialize explicitly after migration; never reset on startup.</summary>
public sealed class RepositorySnapshotVersion
{
    /// <summary>Stable deployment/site/tenant/database identity shared by its replicas.</summary>
    public string SourceId { get; set; } = string.Empty;
    /// <summary>Stable dataset name, independent of projection names and algorithm versions.</summary>
    public string DatasetKey { get; set; } = string.Empty;
    /// <summary>Generation identity; change before serving restored or reset data.</summary>
    public Guid Epoch { get; set; }
    /// <summary>Atomically incremented once per affected managed transaction.</summary>
    public long Revision { get; set; }
}
