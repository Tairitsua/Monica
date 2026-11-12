using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.Locker.Leases;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.Testing.Time;
using NSubstitute;
using Xunit;

namespace Test.Monica.JobScheduler.Services;

public sealed class JobSchedulingHostedServiceShutdownTests
{
    private const string SCOPE = "shutdown-tests";

    [Fact]
    public async Task StopAsync_WhenAdvisoryReleaseFails_ShouldStopAndReleaseTheSchedulerAuthority()
    {
        var token = TestContext.Current.CancellationToken;
        var time = new ControllableTimeProvider(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
        var schedulerStore = new InMemoryJobSchedulerStore(time);
        var external = new InMemoryLeaseStore(time);
        var externalBoundary = Substitute.For<IExclusiveLeaseStore>();
        externalBoundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => external.TryAcquireAsync(call.Arg<LeaseResource>(), call.Arg<string>(), call.Arg<TimeSpan>(), call.Arg<CancellationToken>()));
        externalBoundary.TryRenewAsync(Arg.Any<LeaseIdentity>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => external.TryRenewAsync(call.Arg<LeaseIdentity>(), call.Arg<TimeSpan>(), call.Arg<CancellationToken>()));
        externalBoundary.TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new IOException("The advisory authority is unavailable during shutdown.")));
        await using var host = await new ShutdownFactory(schedulerStore, externalBoundary, time)
            .CreateAsync(cancellationToken: token);
        var service = host.Services.GetRequiredService<JobSchedulingHostedService>();
        await service.RecoverAsync(token);
        await service.CleanupHistoryAsync(token);
        Assert.NotEmpty((await schedulerStore.CoordinatorLeases.GetSnapshotAsync(SCOPE, "jobscheduler:", token)).Leases);
        Assert.Single((await external.GetSnapshotAsync(SCOPE, "jobscheduler:history:", token)).Leases);

        await service.StopAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.Empty((await schedulerStore.CoordinatorLeases.GetSnapshotAsync(SCOPE, "jobscheduler:", token)).Leases);
        await externalBoundary.Received(1).TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>());
        var abandoned = Assert.Single((await external.GetSnapshotAsync(SCOPE, "jobscheduler:history:", token)).Leases);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(abandoned, Assert.Single((await external.GetSnapshotAsync(SCOPE, "jobscheduler:history:", token)).Leases));
        Assert.Empty((await schedulerStore.CoordinatorLeases.GetSnapshotAsync(SCOPE, "jobscheduler:", token)).Leases);
    }

    // This lifecycle scenario discovers the production scheduler assembly, which contains no application jobs.
    private sealed class ShutdownFactory(InMemoryJobSchedulerStore schedulerStore, IExclusiveLeaseStore external, TimeProvider time)
        : MonicaTestApplicationFactory<InMemoryJobSchedulerStore>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            base.ConfigureHost(builder);
            builder.Services.AddKeyedSingleton<IExclusiveLeaseStore>("advisory", external);
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddJobScheduler(options =>
            {
                options.ProjectName = "shutdown-owner";
                options.WorkerPollInterval = TimeSpan.FromDays(1);
                options.SchedulingPollInterval = TimeSpan.FromDays(1);
                options.HistoryCleanupLeaseStore = new("advisory", SCOPE);
            }).UseInMemoryStore().UseSchedulerScope(SCOPE);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.Replace(ServiceDescriptor.Singleton<IJobSchedulerStore>(schedulerStore));
            services.Replace(ServiceDescriptor.Singleton(time));
        }
    }
}
