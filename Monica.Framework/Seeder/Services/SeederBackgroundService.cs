using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Framework.Seeder.Models;
using Monica.Framework.Seeder.Services.Support;
using Monica.Modules;

namespace Monica.Framework.Seeder.Services;

internal sealed class SeederBackgroundService(
    IHostApplicationLifetime applicationLifetime,
    SeederScheduler scheduler,
    SeederState seederState,
    IObservableInstanceRegistry observableRegistry,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<SeederBackgroundService> logger)
    : MoBackgroundService(observableRegistry, hostedServiceOptions, serviceScopeFactory, logger)
{
    public override string ServiceName => nameof(SeederBackgroundService);

    public override string? ServiceGroupId => nameof(ModuleSeeder);

    /// <summary>
    /// Publishes a cancelled run for shutdowns that race ahead of the background loop: modern
    /// <see cref="BackgroundService"/> implementations dispatch <c>ExecuteAsync</c> through
    /// <see cref="Task.Run(Action, CancellationToken)"/>, so a stop that fires before the queued work
    /// item starts would otherwise leave the run in <see cref="SeederRunStatus.Waiting"/> forever.
    /// The stop pipeline always awaits this hook, which makes the pre-start outcome deterministic.
    /// </summary>
    protected override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        seederState.MarkSchedulerCancelledIfWaiting();
        return Task.CompletedTask;
    }

    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        RecordState(
            "Waiting for the application to finish starting before scheduling seeders.",
            HostedServiceState.WaitingDependency);
        try
        {
            await WaitForApplicationStartedAsync(applicationLifetime.ApplicationStarted, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            seederState.MarkSchedulerCancelled();
            return;
        }

        RecordState("Scheduling the seeder dependency graph.", HostedServiceState.Executing);
        await scheduler.RunAsync(stoppingToken).ConfigureAwait(false);

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var snapshot = seederState.GetSnapshot();
        if (snapshot.Status == SeederRunStatus.Aborted)
        {
            RecordState(
                $"Seeder run was aborted by fail-fast seeder '{snapshot.FailFastTriggerSeederTypeName}'.",
                HostedServiceState.Faulted);
            return;
        }

        if (snapshot.RequiredUnsuccessfulCount > 0)
        {
            RecordState(
                $"Seeder run completed with {snapshot.RequiredUnsuccessfulCount} unsuccessful required seeders.",
                HostedServiceState.Faulted);
        }
        else if (snapshot.OptionalUnsuccessfulCount > 0)
        {
            RecordState(
                $"Seeder run completed with {snapshot.OptionalUnsuccessfulCount} unsuccessful optional seeders.",
                HostedServiceState.Degraded);
        }
        else
        {
            RecordState(
                $"Seeder startup batch completed: {snapshot.SucceededCount} succeeded, " +
                $"{snapshot.HeldCount} held for explicit execution, {snapshot.DisabledCount} disabled by placement.",
                HostedServiceState.Running);
        }
    }

    private static async Task WaitForApplicationStartedAsync(
        CancellationToken applicationStarted,
        CancellationToken stoppingToken)
    {
        if (applicationStarted.IsCancellationRequested)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = applicationStarted.Register(static state =>
            ((TaskCompletionSource)state!).TrySetResult(), completion);
        await completion.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
    }
}
