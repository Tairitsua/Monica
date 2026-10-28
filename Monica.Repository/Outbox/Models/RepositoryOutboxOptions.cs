using Monica.Locker.EfCore;
using Monica.Locker.Leases;

namespace Monica.Repository.Outbox.Models;

/// <summary>Controls durable delivery for one repository context. Event contracts come from EventBus metadata.</summary>
public sealed class RepositoryOutboxOptions
{
    /// <summary>Shared coordinator table mapping. Select one migration owner when Outbox and JobScheduler share a database.</summary>
    public LeaseTableOptions LeaseTable { get; } = new();

    /// <summary>Maintenance coordination: 30-second grant, five-second standby/operation bounds. Delivery claims remain independent.</summary>
    public LeaseCoordinatorOptions MaintenanceLease { get; set; } = new();

    /// <summary>
    /// Optional keyed authority for duplicate-tolerant cleanup and statistics. Null uses the local database's
    /// transaction-fenced leases. An external store (for example Redis) suppresses redundant work without a SQL
    /// lease-row guard; retention predicates still protect pending/recent messages. Scope must identify this database.
    /// All replicas sharing a physical Outbox table must select the same authority and scope.
    /// </summary>
    public LeaseStoreSelection? MaintenanceLeaseStore { get; set; }

    /// <summary>Claim duration, five minutes by default. Configure above the transport's normal send timeout.</summary>
    public TimeSpan DeliveryLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum messages claimed per delivery pass, including failed attempts; defaults to 100. Does not limit cleanup.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum simultaneous deliveries per store and drain invocation; defaults to one. Claims only free
    /// delivery slots. Increasing this value overlaps handlers and transport calls in independent scopes;
    /// it does not guarantee ordering, including messages with the same transport key.
    /// </summary>
    public int DeliveryConcurrency { get; set; } = 1;

    /// <summary>Delay after an incomplete delivery batch or a failed scan; defaults to five seconds. Full successful batches continue immediately.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum acknowledged rows deleted by one cleanup statement; defaults to 1,000.</summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>Delay before the first cleanup and between cleanup passes; defaults to one minute, independently of delivery.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum elapsed time before starting another cleanup batch; defaults to two seconds.
    /// Each statement commits independently. This budget does not interrupt an in-flight database command;
    /// configure the provider's command timeout to bound individual commands.
    /// </summary>
    public TimeSpan CleanupTimeBudget { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Delay before the first backlog query and between status samples; defaults to one minute, independently of delivery.</summary>
    public TimeSpan StatusInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Enables the host-owned delivery worker; disable only when another host drains this store or in deterministic tests.</summary>
    public bool EnableWorker { get; set; } = true;

    /// <summary>Initial delay after a failed send; defaults to one second.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum backoff for a pending message; defaults to five minutes.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delivered rows older than this are pruned; defaults to seven days. Pending rows are retained.</summary>
    public TimeSpan DeliveredRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maps the Outbox table for runtime use without owning it in migrations. Set this on contexts that share
    /// a physical database with another context whose migrations create the shared table; leave the default
    /// (owner) on exactly one context per database.
    /// </summary>
    public bool ExcludeFromMigrations { get; set; }
}
