using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services;
using Monica.JobScheduler.Services.Support;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.JobScheduler.Stores.Shared;
using Xunit;

namespace Test.Monica.JobScheduler.Modules;

public sealed class ModuleJobSchedulerActivationTests
{
    private const string FALLBACK_OWNER = "standalone-owner";
    private static readonly DateTimeOffset NOW = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddJobScheduler_WhenActivationSelectsSeveralOwners_ShouldFreezeOwnershipAndExcludeInactiveJobs()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var probe = new ExecutionProbe();
        await using var application = await new ActivationApplicationFactory(store, time, probe, "worker-a")
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await application.Services.GetRequiredService<JobSchedulingHostedService>()
            .ScheduleAsync(TestContext.Current.CancellationToken);

        var definitions = application.Services.GetRequiredService<IReadOnlyList<LocalJobDefinition>>();
        definitions.Should().HaveCount(3);
        var runtime = await application.Services.GetRequiredService<JobSchedulerFacade>()
            .GetRuntimeOverviewAsync(TestContext.Current.CancellationToken);
        runtime.Data!.Configuration.OwnerKeys.Should().BeEquivalentTo(
            [StoreFixture.OWNER_A, StoreFixture.OWNER_B, FALLBACK_OWNER]);
        definitions.Single(definition => definition.JobClrType == typeof(FirstRoleJob)).OwnerKey
            .Should().Be(StoreFixture.OWNER_A);
        definitions.Single(definition => definition.JobClrType == typeof(SecondRoleJob)).OwnerKey
            .Should().Be(StoreFixture.OWNER_B);
        definitions.Single(definition => definition.JobClrType == typeof(StandaloneJob)).OwnerKey
            .Should().Be(FALLBACK_OWNER);
        definitions.Should().OnlyContain(definition => definition.Declaration.JobKey == definition.JobClrType.FullName);
        application.Services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(InactiveRoleJob))
            .Should().BeFalse();
        application.Services.GetRequiredService<JobRegistry>().GetJobClrType(typeof(InactiveRoleJob).FullName!)
            .Should().BeNull();

        var manager = application.Services.GetRequiredService<ITriggeredJobManager>();
        var first = await manager.EnqueueAsync(new FirstArguments(1), cancellationToken: TestContext.Current.CancellationToken);
        var second = await manager.EnqueueAsync(new SecondArguments(2), cancellationToken: TestContext.Current.CancellationToken);
        var standalone = await manager.EnqueueAsync(new StandaloneArguments(3), cancellationToken: TestContext.Current.CancellationToken);
        (await store.GetExecutionAsync(StoreFixture.SCOPE, first, TestContext.Current.CancellationToken))!
            .Template.OwnerKey.Should().Be(StoreFixture.OWNER_A);
        (await store.GetExecutionAsync(StoreFixture.SCOPE, second, TestContext.Current.CancellationToken))!
            .Template.OwnerKey.Should().Be(StoreFixture.OWNER_B);
        (await store.GetExecutionAsync(StoreFixture.SCOPE, standalone, TestContext.Current.CancellationToken))!
            .Template.OwnerKey.Should().Be(FALLBACK_OWNER);

        var enqueueInactive = () => manager.EnqueueAsync(new InactiveArguments(4),
            cancellationToken: TestContext.Current.CancellationToken);
        await enqueueInactive.Should().ThrowAsync<InvalidOperationException>().WithMessage("No local triggered job*");
    }

    [Fact]
    public async Task ClaimAvailableWorkAsync_WhenReplicasShareSeveralOwners_ShouldExecuteSelectedWorkOnceAndLeaveInactiveWorkQueued()
    {
        var time = new StoreFixture.ManualTimeProvider(NOW);
        var store = new InMemoryJobSchedulerStore(time);
        var probe = new ExecutionProbe();
        await using var firstHost = await new ActivationApplicationFactory(store, time, probe, "worker-a")
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var secondHost = await new ActivationApplicationFactory(store, time, probe, "worker-b")
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await firstHost.Services.GetRequiredService<JobSchedulingHostedService>()
            .ScheduleAsync(TestContext.Current.CancellationToken);
        await secondHost.Services.GetRequiredService<JobSchedulingHostedService>()
            .ScheduleAsync(TestContext.Current.CancellationToken);
        await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(StoreFixture.SCOPE, "inactive-owner",
        [
            StoreFixture.TriggeredDeclaration(typeof(InactiveRoleJob).FullName!) with
            {
                JobArgsKey = typeof(InactiveArguments).FullName
            }
        ]), TestContext.Current.CancellationToken);
        await store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = StoreFixture.SCOPE,
            OwnerKey = "inactive-owner",
            JobKey = typeof(InactiveRoleJob).FullName!,
            InstanceId = "inactive-execution",
            JobArgs = "{\"Value\":4}"
        }, TestContext.Current.CancellationToken);
        var manager = firstHost.Services.GetRequiredService<ITriggeredJobManager>();
        await manager.EnqueueAsync(new FirstArguments(1), cancellationToken: TestContext.Current.CancellationToken);
        await manager.EnqueueAsync(new SecondArguments(2), cancellationToken: TestContext.Current.CancellationToken);

        await Task.WhenAll(
            firstHost.Services.GetRequiredService<JobExecutionWorkerHostedService>()
                .ClaimAvailableWorkAsync(TestContext.Current.CancellationToken),
            secondHost.Services.GetRequiredService<JobExecutionWorkerHostedService>()
                .ClaimAvailableWorkAsync(TestContext.Current.CancellationToken));
        await probe.WaitForAsync(typeof(FirstRoleJob), TestContext.Current.CancellationToken);
        await probe.WaitForAsync(typeof(SecondRoleJob), TestContext.Current.CancellationToken);

        probe.Count(typeof(FirstRoleJob)).Should().Be(1);
        probe.Count(typeof(SecondRoleJob)).Should().Be(1);
        probe.Count(typeof(InactiveRoleJob)).Should().Be(0);
        (await store.GetExecutionAsync(StoreFixture.SCOPE, "inactive-execution", TestContext.Current.CancellationToken))!
            .State.Should().Be(JobExecutionState.Queued);
        (await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_A,
            typeof(FirstRoleJob).FullName!, TestContext.Current.CancellationToken))!.IsPresent.Should().BeTrue();
        (await store.GetDefinitionAsync(StoreFixture.SCOPE, StoreFixture.OWNER_B,
            typeof(SecondRoleJob).FullName!, TestContext.Current.CancellationToken))!.IsPresent.Should().BeTrue();
    }

    private sealed class ActivationApplicationFactory(
        InMemoryJobSchedulerStore store, TimeProvider time, ExecutionProbe probe, string workerInstanceId)
        : MonicaTestApplicationFactory<ModuleJobSchedulerActivationTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddModule<ActivationModule, ActivationModuleOptions>(options => options.Policy = new ActivationPolicy());
            builder.AddJobScheduler(options =>
            {
                options.ProjectName = FALLBACK_OWNER;
                options.WorkerInstanceId = workerInstanceId;
                options.WorkerPollInterval = TimeSpan.FromDays(1);
                options.SchedulingPollInterval = TimeSpan.FromDays(1);
                options.EnableHistoryCleanup = false;
            }).UseInMemoryStore().UseSchedulerScope(StoreFixture.SCOPE);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.Replace(ServiceDescriptor.Singleton<IJobSchedulerStore>(store));
            services.Replace(ServiceDescriptor.Singleton(time));
            services.AddSingleton(probe);
        }
    }

    public sealed class ActivationModule : MonicaModule<ActivationModuleOptions>, IApplicationCompositionModule
    {
        public void PrepareApplication(IMonicaBuilder builder, Microsoft.Extensions.Hosting.IHostApplicationBuilder hostBuilder) =>
            Application.SetTypeActivationPolicy(Option.Policy);
        public void RegisterApplication(IMonicaBuilder builder, Microsoft.Extensions.Hosting.IHostApplicationBuilder hostBuilder) { }
    }

    public sealed class ActivationModuleOptions : ModuleOptions<ActivationModule>
    {
        public ITypeActivationPolicy Policy { get; set; } = null!;
    }

    private sealed class ActivationPolicy : ITypeActivationPolicy
    {
        private readonly IReadOnlyDictionary<Type, string?> _owners = new Dictionary<Type, string?>
        {
            [typeof(FirstRoleJob)] = StoreFixture.OWNER_A,
            [typeof(SecondRoleJob)] = StoreFixture.OWNER_B,
            [typeof(StandaloneJob)] = null
        };

        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) =>
            kind != TypeActivationKind.Job || _owners.ContainsKey(implementationType) ? ActivationMode.Automatic : ActivationMode.Disabled;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => _owners.GetValueOrDefault(implementationType);
    }

    private sealed class ExecutionProbe
    {
        private readonly ConcurrentDictionary<Type, int> _counts = new();
        private readonly ConcurrentDictionary<Type, TaskCompletionSource> _started = new();
        public int Count(Type jobType) => _counts.GetValueOrDefault(jobType);
        public void Record(Type jobType)
        {
            _counts.AddOrUpdate(jobType, 1, static (_, count) => count + 1);
            _started.GetOrAdd(jobType, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        public Task WaitForAsync(Type jobType, CancellationToken cancellationToken) => _started
            .GetOrAdd(jobType, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private sealed record FirstArguments(int Value);
    private sealed record SecondArguments(int Value);
    private sealed record StandaloneArguments(int Value);
    private sealed record InactiveArguments(int Value);

    private sealed class FirstRoleJob(ExecutionProbe probe) : ITriggeredJob<FirstArguments>
    {
        public Task ExecuteAsync(FirstArguments args, CancellationToken cancellationToken)
        {
            probe.Record(typeof(FirstRoleJob));
            return Task.CompletedTask;
        }
    }

    private sealed class SecondRoleJob(ExecutionProbe probe) : ITriggeredJob<SecondArguments>
    {
        public Task ExecuteAsync(SecondArguments args, CancellationToken cancellationToken)
        {
            probe.Record(typeof(SecondRoleJob));
            return Task.CompletedTask;
        }
    }

    private sealed class StandaloneJob : ITriggeredJob<StandaloneArguments>
    {
        public Task ExecuteAsync(StandaloneArguments args, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InactiveRoleJob : ITriggeredJob<InactiveArguments>
    {
        public Task ExecuteAsync(InactiveArguments args, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inactive job executed.");
    }
}
