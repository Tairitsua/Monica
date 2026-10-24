using Domains.Ordering.Configurations;
using Domains.Ordering.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Annotations;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Models;

namespace Roles.Ordering.Reporting.BackgroundWorkers;

/// <summary>
/// Reports the current draft-order backlog on a recurring schedule.
/// </summary>
[JobConfig(
    JobName = "Ordering backlog report",
    Description = "Reports the number of draft orders and raises the log level when the configured threshold is reached.",
    CronSchedule = "0 * * * * *",
    MaxConcurrency = 1,
    RetryCount = 1,
    MaxExecutionTimeoutSeconds = 30)]
public sealed class WorkerOrderBacklogReport(
    IRepositoryOrder repository,
    IOptions<OptionsOrdering> options,
    ILogger<WorkerOrderBacklogReport> logger)
    : RecurringJob(logger)
{
    /// <inheritdoc />
    public override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var orders = await repository.ListAsync(cancellationToken);
        var draftOrderCount = orders.Count(static order =>
            order.CaptureSnapshot().Status == OrderStatus.Draft);
        var warningThreshold = options.Value.BacklogWarningThreshold;
        var summary =
            $"Ordering backlog report: {draftOrderCount} draft order(s) out of {orders.Count}; " +
            $"warning threshold {warningThreshold}.";
        var logLevel = draftOrderCount >= warningThreshold
            ? LogLevel.Warning
            : LogLevel.Information;

        Logger.Log(
            logLevel,
            "Ordering backlog contains {DraftOrderCount} draft order(s) out of {OrderCount}; warning threshold is {WarningThreshold}.",
            draftOrderCount,
            orders.Count,
            warningThreshold);

        await RecordExecutionLogAsync(
            summary,
            logLevel,
            cancellationToken: cancellationToken);
    }
}
