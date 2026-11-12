using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.JobScheduler.Modules;

public sealed class ModuleJobSchedulerCompositionTests
{
    [Fact]
    public void AddJobScheduler_WithoutStoreOrScope_ShouldFailFinalization()
    {
        var builder = WebApplication.CreateBuilder();

        var act = () => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddJobScheduler();
        });

        act.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("scheduler scope");
    }

    [Fact]
    public void AutomaticScheduler_WithScopeButNoStore_ShouldRejectMissingServiceContract()
    {
        var builder = WebApplication.CreateBuilder();
        var error = Assert.ThrowsAny<Exception>(() => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddJobScheduler().UseSchedulerScope("missing-store");
        }));
        Assert.Contains("IJobSchedulerStore", error.ToString());
    }

    [Fact]
    public void AddJobScheduler_WithInMemoryStoreAndScope_ShouldRegisterBothPlanesForEveryHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options =>
            {
                options.ExcludeDefault();
                options.Add(typeof(ModuleJobScheduler).Assembly);
            });
            monica.AddJobScheduler(options => options.ProjectName = "owner-a")
                .UseInMemoryStore()
                .UseSchedulerScope("composition-tests");
        });
        using var host = builder.Build();

        host.Services.GetRequiredService<JobSchedulerRuntimeState>().Should().NotBeNull();
        host.Services.GetServices<IHostedService>()
            .Should().Contain(service => service is JobSchedulingHostedService)
            .And.Contain(service => service is JobExecutionWorkerHostedService);
        host.Services.GetRequiredService<JobSchedulingHostedService>().Should().NotBeNull();
        host.Services.GetRequiredService<JobExecutionWorkerHostedService>().Should().NotBeNull();
    }

    [Fact]
    public void ValidateOptions_WithDefaultDurations_ShouldSucceed()
    {
        var act = () => ModuleJobScheduler.ValidateRuntimeOptions(CreateValidOptions());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ValidateOptions_WhenGracePeriodReachesOrExceedsLeaseDuration_ShouldFail(
        bool shutdownGrace,
        bool exceedsLease)
    {
        var options = CreateValidOptions();
        var grace = exceedsLease
            ? options.ExecutionLeaseDuration.Add(TimeSpan.FromSeconds(1))
            : options.ExecutionLeaseDuration;
        if (shutdownGrace)
        {
            options.WorkerShutdownGracePeriod = grace;
        }
        else
        {
            options.ExecutionCancellationGracePeriod = grace;
        }

        var expectedPropertyName = shutdownGrace
            ? nameof(ModuleJobSchedulerOption.WorkerShutdownGracePeriod)
            : nameof(ModuleJobSchedulerOption.ExecutionCancellationGracePeriod);
        var act = () => ModuleJobScheduler.ValidateRuntimeOptions(options);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(expectedPropertyName)
            .And.Contain(nameof(ModuleJobSchedulerOption.ExecutionLeaseDuration));
    }

    private static ModuleJobSchedulerOption CreateValidOptions() => new()
    {
        SchedulerScopeKey = "composition-tests",
        ProjectName = "owner-a",
        WorkerInstanceId = "worker-1"
    };
}
