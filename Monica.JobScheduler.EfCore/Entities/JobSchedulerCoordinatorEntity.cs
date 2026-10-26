namespace Monica.JobScheduler.EfCore.Entities;

internal sealed class JobSchedulerCoordinatorEntity
{
    public string SchedulerScopeKey { get; set; } = null!;
    public string CoordinatorKey { get; set; } = null!;
    public string WorkerInstanceId { get; set; } = null!;
    public string LeaseToken { get; set; } = null!;
    public long ExpiresAtUtcTicks { get; set; }
}
