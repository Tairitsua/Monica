using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.Framework.UI.UIEventBus.Models;

namespace Monica.Framework.UI.UIEventBus.State;

/// <summary>Exclusively controls a scoped service's standalone listener for one mounted component.</summary>
/// <remarks>
/// Obtain a lease through <see cref="EventBusTestService.AcquireStandaloneListenerLeaseAsync"/> and dispose it when
/// its component retires. Drain admitted component operations and attempt <see cref="UnsubscribeAsync"/> before
/// releasing ownership. Disposal serializes behind admitted service operations and permits the next lease to adopt
/// an exact handle retained after failed removal; it does not discard or silently replace that handle. The scoped
/// service owns listener resources and performs its own final cleanup. Never dispose the borrowed service.
/// </remarks>
public sealed class EventBusStandaloneListenerLease : IAsyncDisposable
{
    private readonly EventBusTestService _service;
    private readonly object _retirementLock = new();
    private Task? _releaseTask;
    private int _retired;

    internal EventBusStandaloneListenerLease(EventBusTestService service) => _service = service;

    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>Gets a detached exact handle/topic, or null when this lease controls no current listener or has retired.</summary>
    public EventBusStandaloneListenerSnapshot? Snapshot => _service.GetStandaloneListenerSnapshot(this);

    /// <summary>Starts a standalone listener without replacing an existing handle recovered by this lease.</summary>
    /// <param name="topicName">The exact topic on the default distributed bus.</param>
    /// <returns>The owned handle or a failure; this operation has no cancellation argument and must be drained.</returns>
    public Task<Result<IEventSubscription>> SubscribeAsync(string topicName) => _service.SubscribeForLeaseAsync(this, topicName);

    /// <summary>Attempts removal of this lease's exact listener, retaining it on failure for a retry or later recovery.</summary>
    /// <returns>Success when removed or absent, or a failure. No later owner's listener can be removed by this lease.</returns>
    public Task<Result> UnsubscribeAsync() => _service.UnsubscribeForLeaseAsync(this);

    /// <summary>Retires admission and releases exclusive control after already admitted service operations finish.</summary>
    /// <returns>An idempotent release operation; retained listener resources remain owned by the scoped service.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_retirementLock)
        {
            Volatile.Write(ref _retired, 1);
            return new ValueTask(_releaseTask ??= _service.ReleaseStandaloneLeaseAsync(this));
        }
    }
}
