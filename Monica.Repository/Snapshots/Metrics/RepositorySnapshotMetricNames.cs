namespace Monica.Repository.Snapshots.Metrics;

/// <summary>Standard .NET instruments emitted by repository snapshots. Exporters belong to the host.</summary>
public static class RepositorySnapshotMetricNames
{
    /// <summary>Meter to include in a host's telemetry subscription.</summary>
    public const string MeterName = "Monica.Repository.Snapshots";
    /// <summary>Cache, construction and authority-validation outcomes, tagged by a bounded event name.</summary>
    public const string Events = "monica.repository.snapshots.events";
    /// <summary>Construction duration, including queueing and final authority validation, in milliseconds.</summary>
    public const string BuildDuration = "monica.repository.snapshots.build.duration";
    /// <summary>Primary database authority-validation duration in milliseconds.</summary>
    public const string ValidationDuration = "monica.repository.snapshots.validation.duration";
}
