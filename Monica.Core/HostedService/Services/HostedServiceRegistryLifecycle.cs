using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Abstractions.Internal;
using Monica.Core.HostedService.Services.Support;

namespace Monica.Core.HostedService.Services;

/// <summary>
/// Publishes Monica hosted-service identities before any hosted service starts and owns reversible runtime observers.
/// </summary>
internal sealed class HostedServiceRegistryLifecycle(
    IServiceProvider serviceProvider,
    IHostedServiceRegistryWriter registryWriter,
    IEnumerable<IHostedServiceRuntimeObserver> observers) : IHostedLifecycleService, IDisposable
{
    private readonly Lock _subscriptionLock = new();
    private List<IDisposable> _subscriptions = [];
    private int _starting;
    private int _disposed;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _starting, 1) != 0)
        {
            throw new InvalidOperationException(
                "The hosted-service registry lifecycle cannot be started more than once.");
        }

        var allServices = serviceProvider.GetServices<IHostedService>().ToArray();
        var hostedServices = allServices.OfType<IMoHostedService>().ToArray();
        ValidateDistinctInstances(hostedServices);

        var subscriptions = new List<IDisposable>(hostedServices.Length * 2);
        var runtimeOwners = new List<IHostedServiceRuntimeOwner>(hostedServices.Length);
        try
        {
            // Materialize identity before attaching observers so every observer sees the same immutable runtime identity.
            foreach (var service in hostedServices)
            {
                if (service is IHostedServiceRuntimeOwner runtimeOwner)
                {
                    runtimeOwners.Add(runtimeOwner);
                }

                _ = service.RuntimeInfo;
            }

            foreach (var service in hostedServices)
            {
                foreach (var observer in observers)
                {
                    subscriptions.Add(observer.Observe(service));
                }
            }

            serviceProvider.GetService<HostedServiceRoleParticipant>()?.Bind(allServices);
            registryWriter.Publish(hostedServices);
        }
        catch (Exception exception)
        {
            var observerRollback = DisposeSubscriptions(subscriptions);
            RetainFailedSubscriptions(observerRollback.FailedSubscriptions);
            var rollbackErrors = observerRollback.Errors;
            rollbackErrors.AddRange(ReleaseRuntimeRegistrations(runtimeOwners));
            if (rollbackErrors.Count != 0)
            {
                throw new AggregateException(
                    "Hosted-service registry initialization failed and observer rollback also reported errors.",
                    [exception, .. rollbackErrors]);
            }

            throw;
        }

        lock (_subscriptionLock)
        {
            _subscriptions = subscriptions;
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        var cleanup = DisposeOwnedSubscriptions();
        return cleanup.Errors.Count == 0
            ? Task.CompletedTask
            : Task.FromException(new AggregateException(
                "One or more hosted-service observers failed during shutdown.",
                cleanup.Errors));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            var cleanup = DisposeOwnedSubscriptions();
            if (cleanup.Errors.Count != 0)
            {
                throw new AggregateException(
                    "One or more hosted-service observers failed during host disposal.",
                    cleanup.Errors);
            }
        }
    }

    private static void ValidateDistinctInstances(IReadOnlyList<IMoHostedService> hostedServices)
    {
        var instances = new HashSet<IMoHostedService>(ReferenceEqualityComparer.Instance);
        foreach (var service in hostedServices)
        {
            if (!instances.Add(service))
            {
                throw new InvalidOperationException(
                    $"Hosted service instance '{service.ServiceName}' is registered more than once as IHostedService.");
            }
        }
    }

    private ObserverCleanupResult DisposeOwnedSubscriptions()
    {
        List<IDisposable> subscriptions;
        lock (_subscriptionLock)
        {
            subscriptions = _subscriptions;
            _subscriptions = [];
        }

        var cleanup = DisposeSubscriptions(subscriptions);
        RetainFailedSubscriptions(cleanup.FailedSubscriptions);
        return cleanup;
    }

    private void RetainFailedSubscriptions(IReadOnlyList<IDisposable> failedSubscriptions)
    {
        if (failedSubscriptions.Count == 0)
        {
            return;
        }

        lock (_subscriptionLock)
        {
            _subscriptions = [.. failedSubscriptions, .. _subscriptions];
        }
    }

    private static ObserverCleanupResult DisposeSubscriptions(IReadOnlyList<IDisposable> subscriptions)
    {
        var errors = new List<Exception>();
        var failedSubscriptions = new List<IDisposable>();
        for (var index = subscriptions.Count - 1; index >= 0; index--)
        {
            try
            {
                subscriptions[index].Dispose();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
                failedSubscriptions.Insert(0, subscriptions[index]);
            }
        }

        return new ObserverCleanupResult(errors, failedSubscriptions);
    }

    private static IReadOnlyList<Exception> ReleaseRuntimeRegistrations(
        IReadOnlyList<IHostedServiceRuntimeOwner> runtimeOwners)
    {
        var errors = new List<Exception>();
        for (var index = runtimeOwners.Count - 1; index >= 0; index--)
        {
            try
            {
                runtimeOwners[index].ReleaseRuntimeInfo();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return errors;
    }

    private sealed record ObserverCleanupResult(
        List<Exception> Errors,
        IReadOnlyList<IDisposable> FailedSubscriptions);
}
