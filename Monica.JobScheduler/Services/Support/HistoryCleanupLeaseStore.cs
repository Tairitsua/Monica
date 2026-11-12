using Monica.Locker.Leases;

namespace Monica.JobScheduler.Services.Support;

// Bound once at composition; scheduler work never resolves keyed services dynamically.
internal sealed record HistoryCleanupLeaseStore(IExclusiveLeaseStore Store);
