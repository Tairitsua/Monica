using System.Reflection;
using System.Reflection.Emit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Execution;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.ObservableInstance.Services;
using Monica.Core.Results;
using Monica.Framework.Seeder.Annotations;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Facades;
using Monica.Framework.Seeder.Models;
using Monica.Framework.Seeder.Models.Internal;
using Monica.Framework.Seeder.Services;
using Monica.Framework.Seeder.Services.Support;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.Seeder;

public sealed class ModuleSeederTests
{
    private static readonly TimeSpan HANG_GUARD = TimeSpan.FromSeconds(10);
    private static Assembly OptionalFailFastSeederAssembly { get; } = CreateOptionalFailFastSeederAssembly();

    [Fact]
    public async Task AddSeeder_WhenHostStarts_ShouldRunAfterApplicationStartedAndRegisterReadinessCheck()
    {
        var factory = new SeederTestApplicationFactory();

        await using var application = await factory.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var state = application.Services.GetRequiredService<ISeederState>();
        await WaitFor.UntilAsync(
            _ => Task.FromResult(state.GetSnapshot().IsCompleted),
            cancellationToken: TestContext.Current.CancellationToken);
        var report = await application.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        application.ModuleSnapshots.Should().Contain(static module => module.ModuleType == typeof(ModuleSeeder));
        application.Services.GetRequiredService<SeederFacade>().Should().NotBeNull();
        state.GetSnapshot().StartedAtUtc.Should().NotBeNull();
        report.Entries.Should().ContainKey("monica.seeder");
        report.Entries["monica.seeder"].Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task AddSeeder_WhenOptionalSeederFailsFast_ShouldKeepHostAliveAndFacadeAvailable()
    {
        var factory = new OptionalFailFastSeederTestApplicationFactory();

        await using var application = await factory.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var state = application.Services.GetRequiredService<ISeederState>();
        await WaitFor.UntilAsync(
            _ => Task.FromResult(state.GetSnapshot().Status == SeederRunStatus.Aborted),
            cancellationToken: TestContext.Current.CancellationToken);

        var lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>();
        var result = await application.Services.GetRequiredService<SeederFacade>()
            .GetSnapshotAsync(TestContext.Current.CancellationToken);
        var healthReport = await application.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse();
        lifetime.ApplicationStopped.IsCancellationRequested.Should().BeFalse();
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Run.Status.Should().Be(SeederRunStatus.Aborted);
        result.Data.Run.FailFastTriggerSeederTypeName.Should().Be(
            "Test.Monica.Framework.Seeder.OptionalFailFastHostSeeder");
        var seeder = result.Data.Run.Seeders.Should().ContainSingle().Which;
        seeder.Criticality.Should().Be(SeederCriticality.Optional);
        seeder.FailureBehavior.Should().Be(SeederFailureBehavior.FailFast);
        seeder.Status.Should().Be(SeederStatus.Failed);
        healthReport.Entries["monica.seeder"].Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task SeederBackgroundService_WhenStoppedBeforeApplicationStarted_ShouldPublishCancelledState()
    {
        var options = new ModuleSeederOption();
        var graph = SeederGraph.Create([typeof(PendingHostSeeder)], options);
        var state = new SeederState(graph, TimeProvider.System);
        await using var provider = new ServiceCollection()
            .AddScoped<IExecutionPipeline, PassThroughExecutionPipeline>()
            .BuildServiceProvider();
        using var lifetime = new PendingHostApplicationLifetime();
        var scheduler = new SeederScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            graph,
            state,
            new ImmediateRetryDelay(),
            Options.Create(options),
            NullLogger<SeederScheduler>.Instance);
        using var service = new SeederBackgroundService(
            lifetime,
            scheduler,
            state,
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            Options.Create(new ModuleHostedServiceOption
            {
                DefaultHeartbeatInterval = TimeSpan.Zero
            }),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SeederBackgroundService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken)
            .WaitAsync(HANG_GUARD, TestContext.Current.CancellationToken);
        lifetime.ApplicationStarted.IsCancellationRequested.Should().BeFalse();
        await service.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(HANG_GUARD, TestContext.Current.CancellationToken);

        var snapshot = state.GetSnapshot();
        snapshot.Status.Should().Be(SeederRunStatus.Cancelled);
        snapshot.StartedAtUtc.Should().BeNull();
        snapshot.IsCompleted.Should().BeTrue();
        snapshot.Seeders.Should().ContainSingle().Which.Status.Should().Be(SeederStatus.Cancelled);
    }

    [Fact]
    public async Task SeederBackgroundService_WhenStoppedWithoutEverStarting_ShouldPublishCancelledState()
    {
        // BackgroundService dispatches ExecuteAsync through Task.Run, so a stop that wins the race
        // before the queued work item starts must still publish a cancelled run via the stop pipeline.
        var options = new ModuleSeederOption();
        var graph = SeederGraph.Create([typeof(PendingHostSeeder)], options);
        var state = new SeederState(graph, TimeProvider.System);
        await using var provider = new ServiceCollection()
            .AddScoped<IExecutionPipeline, PassThroughExecutionPipeline>()
            .BuildServiceProvider();
        using var lifetime = new PendingHostApplicationLifetime();
        var scheduler = new SeederScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            graph,
            state,
            new ImmediateRetryDelay(),
            Options.Create(options),
            NullLogger<SeederScheduler>.Instance);
        using var service = new SeederBackgroundService(
            lifetime,
            scheduler,
            state,
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            Options.Create(new ModuleHostedServiceOption
            {
                DefaultHeartbeatInterval = TimeSpan.Zero
            }),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SeederBackgroundService>.Instance);

        await service.StopAsync(TestContext.Current.CancellationToken)
            .WaitAsync(HANG_GUARD, TestContext.Current.CancellationToken);

        var snapshot = state.GetSnapshot();
        snapshot.Status.Should().Be(SeederRunStatus.Cancelled);
        snapshot.StartedAtUtc.Should().BeNull();
        snapshot.IsCompleted.Should().BeTrue();
        snapshot.Seeders.Should().ContainSingle().Which.Status.Should().Be(SeederStatus.Cancelled);
    }

    private sealed class SeederTestApplicationFactory : MonicaTestApplicationFactory<SeederBase>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddSeeder();
        }
    }

    private sealed class OptionalFailFastSeederTestApplicationFactory
        : MonicaTestApplicationFactory<SeederBase>
    {
        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [OptionalFailFastSeederAssembly];

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddSeeder();
        }
    }

    [SeederPolicy(
        Criticality = SeederCriticality.Optional,
        FailureBehavior = SeederFailureBehavior.FailFast)]
    public abstract class OptionalFailFastHostSeederBase : ISeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken)
        {
            return Task.FromException(new InvalidOperationException("Expected host-level fail-fast failure."));
        }
    }

    private sealed class PendingHostSeeder : ISeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ImmediateRetryDelay : ISeederRetryDelay
    {
        public Task DelayAsync(
            int failedAttempt,
            ModuleSeederOption options,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PassThroughExecutionPipeline : IExecutionPipeline
    {
        public Task<TResult> ExecuteAsync<TInput, TResult>(
            ExecutionDescriptor descriptor,
            TInput input,
            object? target,
            ExecutionDelegate<TResult> terminal,
            CancellationToken cancellationToken = default,
            ExecutionFeatureCollection? features = null) => terminal();

        public Task ExecuteAsync<TInput>(
            ExecutionDescriptor descriptor,
            TInput input,
            object? target,
            Func<Task> terminal,
            CancellationToken cancellationToken = default,
            ExecutionFeatureCollection? features = null) => terminal();
    }

    private sealed class PendingHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private static Assembly CreateOptionalFailFastSeederAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Test.Monica.Framework.OptionalFailFastSeeder"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Main");
        var type = module.DefineType(
            "Test.Monica.Framework.Seeder.OptionalFailFastHostSeeder",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(OptionalFailFastHostSeederBase));
        type.DefineDefaultConstructor(MethodAttributes.Public);
        _ = type.CreateType();
        return assembly;
    }
}
