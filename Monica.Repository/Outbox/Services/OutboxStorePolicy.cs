using Monica.Locker.Leases;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

// Values are compared only, never included in diagnostics (endpoints may contain environment details).
internal sealed record OutboxStoreIdentity(string Provider, string DataSource, string Database, string Schema, string Table);

internal sealed record OutboxStorePolicy(
    TimeSpan DeliveryLease, int BatchSize, int DeliveryConcurrency, TimeSpan PollInterval,
    int CleanupBatchSize, TimeSpan CleanupInterval, TimeSpan CleanupTimeBudget, TimeSpan StatusInterval,
    TimeSpan RetryBaseDelay, TimeSpan RetryMaxDelay, TimeSpan DeliveredRetention,
    LeaseCoordinatorOptions MaintenanceLease, LeaseStoreSelection? MaintenanceLeaseStore)
{
    internal static OutboxStorePolicy From(RepositoryOutboxOptions options) => new(
        options.DeliveryLease, options.BatchSize, options.DeliveryConcurrency, options.PollInterval,
        options.CleanupBatchSize, options.CleanupInterval, options.CleanupTimeBudget, options.StatusInterval,
        options.RetryBaseDelay, options.RetryMaxDelay, options.DeliveredRetention,
        options.MaintenanceLease, options.MaintenanceLeaseStore);
}
