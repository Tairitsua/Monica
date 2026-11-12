using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Services.Support;
using Monica.Locker.Leases;

namespace Monica.JobScheduler.Providers;

public sealed partial class InMemoryJobSchedulerStore
{
    private InMemoryLeaseStore? _coordinatorLeases;
    private readonly Dictionary<(string Scope, string Owner), (string Hash, DateTimeOffset Observed, DateTimeOffset Published)> _owners = [];

    /// <inheritdoc />
    public IExclusiveLeaseStore CoordinatorLeases => MemoryLeases;

    private InMemoryLeaseStore MemoryLeases =>
        LazyInitializer.EnsureInitialized(ref _coordinatorLeases, () => new(_timeProvider, _gate));

    /// <inheritdoc />
    public Task<JobCoordinatorSnapshot> GetCoordinatorSnapshotAsync(string schedulerScopeKey,
        CancellationToken cancellationToken = default) =>
        JobCoordinatorLeaseAdapter.SnapshotAsync(CoordinatorLeases, schedulerScopeKey, cancellationToken);

    private void ValidateCoordinatorLeaseUnsafe(JobCoordinatorLeaseKey? key)
    {
        if (key is not null) MemoryLeases.EnsureHeld(key.ToIdentity());
    }

    /// <inheritdoc />
    public Task<string?> GetOwnerManifestHashAsync(string schedulerScopeKey, string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_owners.TryGetValue((schedulerScopeKey, ownerKey), out var owner) ? owner.Hash : null);
        }
    }
}
