using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services.Support;

/// <summary>
/// Activates module-discovered event subscriptions within the Generic Host lifecycle and removes
/// only those subscriptions during shutdown.
/// </summary>
internal sealed class EventBusAutoDiscoveryLifecycle(
    EventBusAutoDiscovery autoDiscovery,
    IEventSubscriptionRegistry subscriptionRegistry,
    IServiceScopeFactory serviceScopeFactory,
    IServiceProviderIsService? registeredServices = null,
    ITypeActivationPolicy? activation = null)
    : IHostedLifecycleService, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IReadOnlyList<EventSubscriptionId> _ownedSubscriptionIds = [];
    private int _disposed;
    private bool _started;

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                throw new InvalidOperationException(
                    "EventBus auto-discovery cannot be started more than once for the same host.");
            }

            var descriptors = autoDiscovery.BuildDescriptors(serviceScopeFactory, registeredServices, activation);
            if (descriptors.Count == 0)
            {
                _started = true;
                return;
            }

            // SubscribeBatchAsync is transactional: cancellation or a partial failure rolls back every
            // subscription created by this call before the startup exception is propagated.
            var subscriptions = await subscriptionRegistry
                .SubscribeBatchAsync(descriptors, cancellationToken)
                .ConfigureAwait(false);
            _ownedSubscriptionIds = subscriptions.Select(static subscription => subscription.Id).ToArray();
            _started = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken)
        => RemoveOwnedSubscriptionsAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        // A cancelled StoppingAsync may have left subscriptions behind. By StoppedAsync every provider
        // has completed StopAsync, so perform a final idempotent in-memory cleanup without cancellation.
        return RemoveOwnedSubscriptionsAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            RemoveOwnedSubscriptionsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await RemoveOwnedSubscriptionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    private async Task RemoveOwnedSubscriptionsAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ownedSubscriptionIds.Count == 0)
            {
                return;
            }

            var subscriptionIds = _ownedSubscriptionIds.Reverse().ToArray();
            try
            {
                await subscriptionRegistry
                    .UnsubscribeBatchAsync(subscriptionIds, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // UnsubscribeBatchAsync attempts every entry. Retain only entries that remain in the
                // registry so a later lifecycle callback can retry cancellation-interrupted cleanup.
                _ownedSubscriptionIds = _ownedSubscriptionIds
                    .Where(subscriptionId => subscriptionRegistry.GetById(subscriptionId) is not null)
                    .ToArray();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
