using System.Text.Json;
using Microsoft.Extensions.Options;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.Modules;

namespace Monica.JobScheduler.Services;

/// <summary>
/// Admits local typed triggered jobs directly into the durable queue under each definition's logical owner.
/// </summary>
internal sealed class TriggeredJobManager(
    IReadOnlyList<LocalJobDefinition> localDefinitions,
    IJobSchedulerStore store,
    IOptions<ModuleJobSchedulerOption> options,
    TimeProvider timeProvider) : ITriggeredJobManager
{
    private readonly IReadOnlyDictionary<Type, LocalJobDefinition> _jobsByArguments = localDefinitions
        .Where(static definition => definition.JobArgsClrType is not null)
        .ToDictionary(static definition => definition.JobArgsClrType!);

    /// <inheritdoc />
    public async Task<string> EnqueueAsync<TArgs>(
        TArgs args,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (delay is { } value && value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), value, "Delay cannot be negative.");
        }

        if (!_jobsByArguments.TryGetValue(typeof(TArgs), out var localDefinition))
        {
            throw new InvalidOperationException(
                $"No local triggered job accepts arguments of type '{typeof(TArgs).FullName}'.");
        }

        var schedulerOptions = options.Value;
        var instanceId = Guid.NewGuid().ToString("N");
        await store.EnqueueAsync(new JobEnqueueRequest
        {
            InstanceId = instanceId,
            SchedulerScopeKey = schedulerOptions.SchedulerScopeKey,
            OwnerKey = localDefinition.OwnerKey ?? schedulerOptions.GetProjectName(),
            JobKey = localDefinition.Declaration.JobKey,
            JobArgs = JsonSerializer.Serialize(args, schedulerOptions.JobArgsSerializerOptions),
            AvailableAtUtc = timeProvider.GetUtcNow().Add(delay ?? TimeSpan.Zero),
            EnqueueReason = delay is null
                ? "Triggered execution enqueued"
                : $"Triggered execution delayed by {delay.Value}"
        }, cancellationToken);
        return instanceId;
    }

    /// <inheritdoc />
    public Task<JobCancellationResult> CancelExecutionAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        return store.RequestCancellationAsync(
            options.Value.SchedulerScopeKey,
            instanceId,
            "Cancellation requested by application",
            cancellationToken);
    }
}
