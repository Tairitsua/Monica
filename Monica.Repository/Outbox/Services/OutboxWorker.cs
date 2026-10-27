using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Modules;
using Monica.Locker.Leases;

namespace Monica.Repository.Outbox.Services;

internal sealed class OutboxWorker(
    IObservableInstanceRegistry registry,
    IOptions<ModuleHostedServiceOption> hostedOptions,
    IServiceScopeFactory scopes,
    ILogger<OutboxWorker> logger,
    TimeProvider time,
    IEnumerable<IOutboxStore> stores)
    : MoBackgroundService(registry, hostedOptions, scopes, logger)
{
    private readonly IOutboxStore[] _stores = stores.ToArray();

    public override string ServiceName => "MonicaOutbox";

    protected override Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
        => Task.WhenAll(_stores.Select(store => RunStoreAsync(store, stoppingToken)));

    private async Task RunStoreAsync(IOutboxStore store, CancellationToken stoppingToken)
    {
        OutboxStoreStatus? previousStatus = null;
        await using var coordinator = store.CreateMaintenanceCoordinator();
        // Each operation owns a fresh scope. Slow maintenance or another context's transport
        // must not hold up delivery. The host awaits all loops during shutdown.
        await Task.WhenAll(
            DeliverAsync(store, stoppingToken),
            RunMaintenanceAsync(store, coordinator, store.CleanupInterval, "cleanup", async (identity, token) =>
            {
                var cleaned = await store.CleanupAsync(identity, token);
                if (cleaned != 0)
                    RecordState($"{store.StoreName}: pruned {cleaned} acknowledged outbox messages", logLevel: LogLevel.Debug);
            }, stoppingToken),
            RunMaintenanceAsync(store, coordinator, store.StatusInterval, "status sampling", async (_, token) =>
            {
                var status = await store.GetStatusAsync(token);
                ReportStatus(store, status, previousStatus);
                previousStatus = status;
            }, stoppingToken));
    }

    private async Task DeliverAsync(IOutboxStore store, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delivered = 0;
            try
            {
                delivered = await store.DrainAsync(store.BatchSize, stoppingToken);
                if (delivered != 0)
                    RecordState($"{store.StoreName}: delivered {delivered} outbox messages", logLevel: LogLevel.Debug);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                RecordState($"{store.StoreName}: outbox scan failed; retrying after its polling interval",
                    exception: exception, logLevel: LogLevel.Error);
            }

            if (delivered < store.BatchSize)
                await Task.Delay(store.PollInterval, time, stoppingToken);
            else
                await Task.Yield();
        }
    }

    private async Task RunMaintenanceAsync(IOutboxStore store, LeaseCoordinator coordinator, TimeSpan interval, string operation,
        Func<LeaseIdentity, CancellationToken, Task> action, CancellationToken stoppingToken)
    {
        var delay = interval;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Maintenance has its own cadence and does not scan the database at host startup.
            await Task.Delay(delay, time, stoppingToken);
            LeaseContext? context = null;
            try
            {
                context = await coordinator.GetAsync(await store.GetMaintenanceResourceAsync(stoppingToken), stoppingToken);
                if (context is null)
                {
                    delay = store.MaintenanceRetryInterval;
                    continue;
                }
                delay = interval;
                using var term = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, context.StoppingToken);
                await action(context.Identity, term.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (context?.StoppingToken.IsCancellationRequested == true)
            {
                delay = store.MaintenanceRetryInterval;
            }
            catch (Exception exception)
            {
                RecordState($"{store.StoreName}: outbox {operation} failed; retrying after its maintenance interval",
                    exception: exception, logLevel: LogLevel.Error);
            }
        }
    }

    private void ReportStatus(IOutboxStore store, OutboxStoreStatus status, OutboxStoreStatus? previousStatus)
    {
        if (status.PendingCount == 0)
        {
            if (previousStatus?.PendingCount > 0)
                RecordState($"{store.StoreName}: no pending outbox messages", logLevel: LogLevel.Debug);
            return;
        }
        var age = status.OldestPendingAtUtc is { } oldest ? time.GetUtcNow().UtcDateTime - oldest : TimeSpan.Zero;
        RecordState($"{store.StoreName}: {status.PendingCount} pending outbox messages; oldest {age:g}; " +
            (status.LastError is null ? "no delivery failure" : $"last failure: {status.LastError}"),
            logLevel: status.LastError is null ? LogLevel.Debug : LogLevel.Warning);
    }
}
