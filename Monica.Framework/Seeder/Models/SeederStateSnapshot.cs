using System.Collections.Immutable;

namespace Monica.Framework.Seeder.Models;

/// <summary>
/// Provides an immutable view of the latest batch and every seeder's current-process outcome.
/// </summary>
public sealed record SeederStateSnapshot
{
    /// <summary>Gets when this snapshot was captured.</summary>
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Gets the current lifecycle state of the complete run.</summary>
    public required SeederRunStatus Status { get; init; }

    /// <summary>Gets when the latest automatic or explicit batch began, or <see langword="null"/> before startup.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>Gets when the latest batch finished, or <see langword="null"/> while it is active. Held work remains unsatisfied.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>Gets the elapsed run duration at snapshot capture time, or <see langword="null"/> before scheduling begins.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Gets the full type name of the deterministic fail-fast trigger while aborting or after abort.</summary>
    public string? FailFastTriggerSeederTypeName { get; init; }

    /// <summary>Gets the discovered seeders ordered by their full type names.</summary>
    public ImmutableArray<SeederExecutionSnapshot> Seeders { get; init; } = [];

    /// <summary>Gets the total number of discovered seeders.</summary>
    public int TotalCount => Seeders.Length;

    /// <summary>Gets the number of seeders waiting to run.</summary>
    public int PendingCount => Count(SeederStatus.Pending);

    /// <summary>Gets the number of seeders currently running.</summary>
    public int RunningCount => Count(SeederStatus.Running);

    /// <summary>Gets the number of successful seeders.</summary>
    public int SucceededCount => Count(SeederStatus.Succeeded);

    /// <summary>Gets the number of failed seeders.</summary>
    public int FailedCount => Count(SeederStatus.Failed);

    /// <summary>Gets the number of dependency-blocked seeders.</summary>
    public int BlockedCount => Count(SeederStatus.Blocked);

    /// <summary>Gets the number of cancelled seeders.</summary>
    public int CancelledCount => Count(SeederStatus.Cancelled);

    /// <summary>Gets the number of seeders awaiting explicit execution or an unmet prerequisite.</summary>
    public int HeldCount => Count(SeederStatus.Held);

    /// <summary>Gets the number of discovered seeders unavailable in this placement.</summary>
    public int DisabledCount => Count(SeederStatus.Disabled);

    /// <summary>Gets the number of seeders in a terminal state.</summary>
    public int CompletedCount => SucceededCount + FailedCount + BlockedCount + CancelledCount;

    /// <summary>Gets the number of required seeders that have not succeeded.</summary>
    public int RequiredUnsuccessfulCount => Seeders.Count(static seeder =>
        seeder.ParticipatesInReadiness &&
        seeder.Criticality == SeederCriticality.Required && seeder.Status != SeederStatus.Succeeded);

    /// <summary>Gets the number of optional seeders that ended unsuccessfully.</summary>
    public int OptionalUnsuccessfulCount => Seeders.Count(static seeder =>
        seeder.Criticality == SeederCriticality.Optional &&
        seeder.Status is SeederStatus.Failed or SeederStatus.Blocked or SeederStatus.Cancelled);

    /// <summary>Gets the current Seeder contribution to application readiness.</summary>
    public SeederReadinessStatus ReadinessStatus
    {
        get
        {
            if (Status is SeederRunStatus.Aborting or SeederRunStatus.Aborted || RequiredUnsuccessfulCount > 0 ||
                Seeders.Any(static seeder => seeder.FailureBehavior == SeederFailureBehavior.FailFast &&
                    seeder.Status == SeederStatus.Failed))
            {
                return SeederReadinessStatus.Unhealthy;
            }

            return OptionalUnsuccessfulCount > 0
                ? SeederReadinessStatus.Degraded
                : SeederReadinessStatus.Healthy;
        }
    }

    /// <summary>Gets whether startup or the latest batch finished. This does not imply that held seeders succeeded.</summary>
    public bool IsCompleted => Status is SeederRunStatus.Succeeded or
        SeederRunStatus.CompletedWithFailures or
        SeederRunStatus.Aborted or
        SeederRunStatus.Cancelled or SeederRunStatus.Held;

    private int Count(SeederStatus status) => Seeders.Count(seeder => seeder.Status == status);
}
