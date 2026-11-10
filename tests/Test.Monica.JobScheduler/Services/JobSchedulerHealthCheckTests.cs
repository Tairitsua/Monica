using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monica.JobScheduler.Services.Support;
using Xunit;

namespace Test.Monica.JobScheduler.Services;

public sealed class JobSchedulerHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenWorkerTemporarilyPauses_ShouldDegradeUntilRecoveryWithoutMaskingPermanentFailures()
    {
        var state = new JobSchedulerRuntimeState();
        state.SetScheduling(true, "ready");
        state.SetWorker(false, "store cleanup");
        state.SetWorkerAdmissionPaused(true);
        var check = new JobSchedulerHealthCheck(state);
        var token = TestContext.Current.CancellationToken;

        (await check.CheckHealthAsync(new HealthCheckContext(), token)).Status.Should().Be(HealthStatus.Degraded);
        state.SetScheduling(false, "coordinator failure");
        (await check.CheckHealthAsync(new HealthCheckContext(), token)).Status.Should().Be(HealthStatus.Unhealthy);
        state.SetScheduling(true, "ready");
        state.SetWorkerAdmissionPaused(false);
        (await check.CheckHealthAsync(new HealthCheckContext(), token)).Status.Should().Be(HealthStatus.Unhealthy);
        state.SetWorkerAdmissionPaused(true);
        state.SetWorker(true, "recovered");
        state.WorkerAdmissionPaused.Should().BeFalse();
        (await check.CheckHealthAsync(new HealthCheckContext(), token)).Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenAPlaneHasNotInitialized_ShouldBeUnhealthy()
    {
        var runtimeState = new JobSchedulerRuntimeState();
        var healthCheck = new JobSchedulerHealthCheck(runtimeState);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("Scheduling has not initialized");
        result.Description.Should().Contain("Worker has not initialized");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenBothPlanesAreReady_ShouldBeHealthy()
    {
        var runtimeState = new JobSchedulerRuntimeState();
        _ = runtimeState.SetScheduling(true, "ready");
        _ = runtimeState.SetWorker(true, "ready");
        var healthCheck = new JobSchedulerHealthCheck(runtimeState);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenSchedulingFails_ShouldReportItsMessage()
    {
        var runtimeState = new JobSchedulerRuntimeState();
        _ = runtimeState.SetWorker(true, "ready");
        _ = runtimeState.SetScheduling(false, "store unreachable");
        var healthCheck = new JobSchedulerHealthCheck(runtimeState);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("store unreachable");
    }
}
