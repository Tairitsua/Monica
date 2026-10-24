using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Annotations;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Modules;

public sealed class ManualJobSchedulerTests
{
    private const string SCOPE = "manual-tests";
    private const string OWNER = "local-owner";

    [Fact]
    public async Task ManualHost_ShouldRunExplicitRecurringWorkLocallyWithoutOpeningSharedStore()
    {
        var probe = new Probe();
        var time = new StoreFixture.ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        await using var host = await new ManualFactory(probe, time, polling: true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var store = Assert.IsType<InMemoryJobSchedulerStore>(host.Services.GetRequiredService<IJobSchedulerStore>());
        time.Advance(TimeSpan.FromDays(2));
        await host.Services.GetRequiredService<JobSchedulingHostedService>().ScheduleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, (await store.QueryExecutionsAsync(new JobExecutionQuery { SchedulerScopeKey = SCOPE }, TestContext.Current.CancellationToken)).TotalCount);
        var facade = host.Services.GetRequiredService<JobSchedulerFacade>();
        var result = await facade.RunRecurringNowAsync(new JobRecurringRunNowRequest
        {
            OwnerKey = OWNER, JobKey = typeof(Recurring).FullName!, InstanceId = "explicit-recurring"
        }, TestContext.Current.CancellationToken);
        Assert.Equal(ResStatus.Ok, result.Status);
        await probe.RecurringCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.RecurringRuns);
        Assert.Equal(0, probe.SharedOpens);
        var overview = (await facade.GetRuntimeOverviewAsync(TestContext.Current.CancellationToken)).Data!;
        Assert.False(overview.Configuration.AutonomousWork);
        Assert.Equal("InMemory", overview.Store.Kind);
        Assert.True(overview.LocalPlane.SchedulingReady);
        Assert.Contains(host.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.ImplementationType == typeof(Recurring) && resource.Mode == ActivationMode.Manual);
        await using var other = await new ManualFactory(new Probe(), time, polling: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotSame(store, other.Services.GetRequiredService<IJobSchedulerStore>());
    }

    [Fact]
    public async Task ManualHost_ShouldPreserveDelayAndRetryThroughNativeExecutionWorker()
    {
        var probe = new Probe();
        var time = new StoreFixture.ManualTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        await using var host = await new ManualFactory(probe, time, polling: false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var store = host.Services.GetRequiredService<IJobSchedulerStore>();
        var worker = host.Services.GetRequiredService<JobExecutionWorkerHostedService>();
        // Drive native claims explicitly so the background loop cannot race the controlled clock.
        await worker.StopAsync(TestContext.Current.CancellationToken);
        var instance = await host.Services.GetRequiredService<ITriggeredJobManager>().EnqueueAsync(new Arguments(7),
            delay: TimeSpan.FromHours(1), cancellationToken: TestContext.Current.CancellationToken);
        async Task<IReadOnlyList<JobExecutionLease>> Claim() => await store.ClaimAsync(new JobClaimRequest
        {
            SchedulerScopeKey = SCOPE, OwnerKey = OWNER, WorkerInstanceId = "test-worker",
            JobKeys = [typeof(Retrying).FullName!], LeaseDuration = TimeSpan.FromMinutes(1), MaxCount = 1
        }, TestContext.Current.CancellationToken);
        Assert.Empty(await Claim());
        Assert.Equal(0, probe.TriggeredRuns);
        time.Advance(TimeSpan.FromHours(1));
        await worker.ExecuteLeaseAsync(Assert.Single(await Claim()), TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.TriggeredRuns);
        Assert.Equal(JobExecutionState.Queued, (await store.GetExecutionAsync(SCOPE, instance, TestContext.Current.CancellationToken))!.State);
        Assert.Empty(await Claim());
        time.Advance(TimeSpan.FromMinutes(1));
        await worker.ExecuteLeaseAsync(Assert.Single(await Claim()), TestContext.Current.CancellationToken);
        Assert.Equal(2, probe.TriggeredRuns);
        Assert.Equal(JobExecutionState.Succeeded, (await store.GetExecutionAsync(SCOPE, instance, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(0, probe.SharedOpens);
    }

    private sealed class ManualFactory(Probe probe, TimeProvider time, bool polling) : MonicaTestApplicationFactory<ManualJobSchedulerTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => { options.AutonomousWork = false; options.DeploymentScope = SCOPE; });
            builder.ConfigureModuleSystem(options => { options.AutoAddMonicaHttpListener = false; options.EnableMinimalApiByDefault = false; });
            builder.AddModule<Selection, SelectionOptions>();
            builder.AddJobScheduler(options =>
            {
                options.ProjectName = OWNER;
                options.WorkerPollInterval = polling ? TimeSpan.FromMilliseconds(5) : TimeSpan.FromDays(1);
                options.ExecutionRetryDelay = TimeSpan.FromMinutes(1);
            }).PostConfigureServices(context => context.Services.AddSingleton<IJobSchedulerStore>(_ =>
            {
                Interlocked.Increment(ref probe.SharedOpens);
                throw new InvalidOperationException("The configured shared provider must not be opened by a Manual host.");
            }));
        }
        protected override void ConfigureHost(Microsoft.AspNetCore.Builder.WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton(probe);
            builder.Services.AddSingleton(time);
        }
    }

    public sealed class Selection : MonicaModule<SelectionOptions>, IApplicationCompositionModule
    {
        public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) => Application.SetTypeActivationPolicy(new Policy());
        public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) { }
    }
    public sealed class SelectionOptions : ModuleOptions<Selection>;
    private sealed class Policy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) =>
            kind == TypeActivationKind.Job && implementationType != typeof(Recurring) && implementationType != typeof(Retrying)
                ? ActivationMode.Disabled : kind is TypeActivationKind.Service or TypeActivationKind.RequestHandler or TypeActivationKind.Endpoint
                    ? ActivationMode.Automatic : ActivationMode.Manual;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }
    private sealed class Probe
    {
        internal int SharedOpens;
        internal int RecurringRuns;
        internal int TriggeredRuns;
        internal TaskCompletionSource RecurringCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record Arguments(int Value);
    [JobConfig(CronSchedule = "* * * * * *")]
    private sealed class Recurring(Probe probe) : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.RecurringRuns);
            probe.RecurringCompleted.TrySetResult();
            return Task.CompletedTask;
        }
    }
    [JobConfig(RetryCount = 1)]
    private sealed class Retrying(Probe probe) : ITriggeredJob<Arguments>
    {
        public Task ExecuteAsync(Arguments args, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref probe.TriggeredRuns) == 1) throw new InvalidOperationException("Retry this explicit job.");
            return Task.CompletedTask;
        }
    }
}
