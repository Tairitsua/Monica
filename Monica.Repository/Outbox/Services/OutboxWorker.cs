using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Modules;

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

    private Task RunStoreAsync(IOutboxStore store, CancellationToken stoppingToken)
    {
        OutboxStoreStatus? previousStatus = null;
        // Each operation owns a fresh scope. Slow maintenance or another context's transport
        // must not hold up delivery. The host awaits all loops during shutdown.
        return Task.WhenAll(
            DeliverAsync(store, stoppingToken),
            RunMaintenanceAsync(store, store.CleanupInterval, "cleanup", async token =>
            {
                var cleaned = await store.CleanupAsync(token);
                if (cleaned != 0)
                    RecordState($"{store.StoreName}: pruned {cleaned} acknowledged outbox messages", logLevel: LogLevel.Debug);
            }, stoppingToken),
            RunMaintenanceAsync(store, store.StatusInterval, "status sampling", async token =>
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

    private async Task RunMaintenanceAsync(IOutboxStore store, TimeSpan interval, string operation,
        Func<CancellationToken, Task> action, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Maintenance has its own cadence and does not scan the database at host startup.
            await Task.Delay(interval, time, stoppingToken);
            try
            {
                await action(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
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
