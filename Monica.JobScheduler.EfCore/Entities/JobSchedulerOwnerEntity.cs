namespace Monica.JobScheduler.EfCore.Entities;

internal sealed class JobSchedulerOwnerEntity
{
    public string SchedulerScopeKey { get; set; } = null!;
    public string OwnerKey { get; set; } = null!;
    public string ManifestHash { get; set; } = null!;
    public long ManifestPublishedAtUtcTicks { get; set; }
    public long LastObservedAtUtcTicks { get; set; }
}
