using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Monica.JobScheduler.Services.Support;

/// <summary>
/// Reports whether the local scheduling and execution responsibilities have reached a usable state.
/// </summary>
internal sealed class JobSchedulerHealthCheck(JobSchedulerRuntimeState runtimeState) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (runtimeState.SchedulingReady && runtimeState.WorkerReady)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "JobScheduler scheduling and execution responsibilities are ready."));
        }

        if (runtimeState.SchedulingReady && runtimeState.WorkerAdmissionPaused)
        {
            return Task.FromResult(HealthCheckResult.Degraded(runtimeState.WorkerMessage));
        }

        var reasons = new[]
            {
                runtimeState.SchedulingReady ? null : runtimeState.SchedulingMessage ?? "Scheduling has not initialized.",
                runtimeState.WorkerReady ? null : runtimeState.WorkerMessage ?? "Worker has not initialized."
            }
            .Where(static reason => reason is not null);
        return Task.FromResult(HealthCheckResult.Unhealthy(string.Join(" ", reasons)));
    }
}
