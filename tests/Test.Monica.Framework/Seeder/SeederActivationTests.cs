using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Annotations;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Annotations;
using Monica.Framework.Seeder.Facades;
using Monica.Framework.Seeder.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.Seeder;

public sealed class SeederActivationTests
{
    private static readonly Assembly ScenarioAssembly = CreateAssembly();
    private static Type SeederType(string name) => ScenarioAssembly.GetType($"Scenario.Seeders.{name}")!;
    private static string Identity(string name) => SeederType(name).FullName!;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Startup_WhenManual_ShouldKeepDefinitionsHeldWithoutReadinessFailure()
    {
        await using var application = await CreateAsync(new Factory());
        var state = application.Services.GetRequiredService<ISeederState>();
        Assert.Equal(5, state.GetSnapshot().HeldCount);
        Assert.Equal(SeederRunStatus.Held, state.GetSnapshot().Status);
        Assert.Equal(SeederReadinessStatus.Healthy, state.GetSnapshot().ReadinessStatus);
        Assert.False(state.IsSucceeded(SeederType("Target")));
        Assert.False(state.IsSucceeded(typeof(string)));
        Assert.Empty(application.Services.GetRequiredService<Probe>().Calls);
    }

    [Fact]
    public async Task RunAsync_WhenTargetIsHeld_ShouldExecuteOnlyUnsatisfiedClosure()
    {
        await using var application = await CreateAsync(new Factory());
        var executor = application.Services.GetRequiredService<ISeederExecutor>();
        var state = application.Services.GetRequiredService<ISeederState>();
        await executor.RunAsync(Identity("Foundation"), Token);
        await executor.RunAsync(Identity("Target"), Token);
        Assert.Equal(["Foundation", "Target"], application.Services.GetRequiredService<Probe>().Calls.ToArray());
        Assert.True(state.IsSucceeded(SeederType("Target")));
        Assert.False(state.IsSucceeded(SeederType("Independent")));
        Assert.Equal(3, state.GetSnapshot().HeldCount);
        Assert.Equal(SeederReadinessStatus.Healthy, state.GetSnapshot().ReadinessStatus);
    }

    [Fact]
    public async Task RunAsync_WhenDependencyIsDisabled_ShouldRejectWithoutResolvingAnySeeder()
    {
        await using var application = await CreateAsync(new Factory(disabledFoundation: true));
        var executor = application.Services.GetRequiredService<ISeederExecutor>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RunAsync(Identity("Target"), Token));
        Assert.Contains("Foundation", exception.Message);
        Assert.Contains("disabled", exception.Message);
        Assert.Empty(application.Services.GetRequiredService<Probe>().Calls);
        Assert.False(application.Services.GetRequiredService<IServiceProviderIsService>().IsService(SeederType("Foundation")));
        Assert.Equal(1, application.Services.GetRequiredService<ISeederState>().GetSnapshot().DisabledCount);
    }

    [Fact]
    public async Task RunAsync_WhenRequiredFailureIsRetried_ShouldPreserveFailureUntilSuccessfulRetry()
    {
        await using var application = await CreateAsync(new Factory());
        var executor = application.Services.GetRequiredService<ISeederExecutor>();
        var state = application.Services.GetRequiredService<ISeederState>();
        var probe = application.Services.GetRequiredService<Probe>();
        probe.FailTarget = true;
        await executor.RunAsync(Identity("Target"), Token);
        Assert.Equal(SeederReadinessStatus.Unhealthy, state.GetSnapshot().ReadinessStatus);
        await executor.RunAsync(Identity("Independent"), Token);
        Assert.Equal(SeederReadinessStatus.Unhealthy, state.GetSnapshot().ReadinessStatus);
        probe.FailTarget = false;
        await executor.RunAsync(Identity("Target"), Token);
        Assert.Equal(["Foundation", "Target", "Independent", "Target"], probe.Calls.ToArray());
        Assert.Equal(SeederReadinessStatus.Healthy, state.GetSnapshot().ReadinessStatus);
        var target = state.GetSnapshot().Seeders.Single(seeder => seeder.SeederTypeName == Identity("Target"));
        Assert.Equal(2, target.Attempts);
        Assert.Equal([SeederAttemptStatus.Failed, SeederAttemptStatus.Succeeded], target.AttemptHistory.Select(attempt => attempt.Status));
    }

    [Fact]
    public async Task RunAsync_WhenBatchOverlaps_ShouldRejectSecondBatchThroughFacade()
    {
        await using var application = await CreateAsync(new Factory());
        var probe = application.Services.GetRequiredService<Probe>();
        probe.BlockTarget = true;
        var running = application.Services.GetRequiredService<ISeederExecutor>().RunAsync(Identity("Target"), Token);
        await probe.TargetStarted.Task.WaitAsync(Token);
        try
        {
            var result = await application.Services.GetRequiredService<SeederFacade>().RunAsync(Identity("Independent"), Token);
            Assert.False(result.IsOk());
            Assert.Contains("already running", result.Message);
            Assert.DoesNotContain("Independent", probe.Calls);
        }
        finally
        {
            probe.ReleaseTarget.TrySetResult();
            await running;
        }
    }

    [Fact]
    public async Task Startup_WhenProcessLocalDependsOnManual_ShouldHoldUntilExplicitClosureRuns()
    {
        await using var application = await CreateAsync(new Factory(standalone: true));
        var state = application.Services.GetRequiredService<ISeederState>();
        var executor = application.Services.GetRequiredService<ISeederExecutor>();
        var local = state.GetSnapshot().Seeders.Single(seeder => seeder.SeederTypeName == Identity("LocalInitialization"));
        Assert.Equal(ActivationMode.Automatic, local.Activation);
        Assert.Equal(SeederStatus.Held, local.Status);
        Assert.Contains("Foundation", local.ActivationReason);
        Assert.Equal(SeederReadinessStatus.Unhealthy, state.GetSnapshot().ReadinessStatus);
        Assert.True(state.IsSucceeded(SeederType("LocalIndependent")));
        await executor.RunAsync(Identity("Foundation"), Token);
        Assert.False(state.IsSucceeded(SeederType("LocalInitialization")));
        await executor.RunAsync(Identity("LocalInitialization"), Token);
        Assert.Equal(["LocalIndependent", "Foundation", "LocalInitialization"], application.Services.GetRequiredService<Probe>().Calls.ToArray());
        Assert.Equal(SeederReadinessStatus.Healthy, state.GetSnapshot().ReadinessStatus);
    }

    [Fact]
    public async Task Startup_WhenProcessLocalDependsOnDisabled_ShouldRetainBlockedReadinessAndRejectRun()
    {
        await using var application = await CreateAsync(new Factory(disabledFoundation: true, localAutomatic: true));
        var state = application.Services.GetRequiredService<ISeederState>();
        var local = state.GetSnapshot().Seeders.Single(seeder => seeder.SeederTypeName == Identity("LocalInitialization"));
        Assert.Equal(SeederStatus.Held, local.Status);
        Assert.Contains("Disabled", local.ActivationReason);
        Assert.Equal(SeederReadinessStatus.Unhealthy, state.GetSnapshot().ReadinessStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => application.Services.GetRequiredService<ISeederExecutor>()
            .RunAsync(Identity("LocalInitialization"), Token));
        Assert.Empty(application.Services.GetRequiredService<Probe>().Calls);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_ShouldRetainRequiredUnreadyOutcome()
    {
        await using var application = await CreateAsync(new Factory());
        var probe = application.Services.GetRequiredService<Probe>();
        probe.BlockTarget = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var execution = application.Services.GetRequiredService<ISeederExecutor>().RunAsync(Identity("Target"), cancellation.Token);
        await probe.TargetStarted.Task.WaitAsync(Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        var snapshot = application.Services.GetRequiredService<ISeederState>().GetSnapshot();
        Assert.Equal(SeederStatus.Cancelled, snapshot.Seeders.Single(seeder => seeder.SeederTypeName == Identity("Target")).Status);
        Assert.Equal(SeederReadinessStatus.Unhealthy, snapshot.ReadinessStatus);
    }

    [Fact]
    public async Task Startup_WhenAutomatic_ShouldExecuteTheEligibleGraph()
    {
        await using var application = await CreateAsync(new Factory(automatic: true));
        var state = application.Services.GetRequiredService<ISeederState>();
        Assert.True(state.IsSucceeded(SeederType("Foundation")));
        Assert.True(state.IsSucceeded(SeederType("Target")));
        Assert.Equal(SeederReadinessStatus.Healthy, state.GetSnapshot().ReadinessStatus);
        Assert.Equal(5, state.GetSnapshot().SucceededCount);
    }

    private static async Task<MonicaTestApplication> CreateAsync(Factory factory)
    {
        var application = await factory.CreateAsync(cancellationToken: Token);
        await WaitFor.UntilAsync(_ => Task.FromResult(application.Services.GetRequiredService<ISeederState>().GetSnapshot().IsCompleted),
            cancellationToken: Token);
        return application;
    }

    private sealed class Factory(bool disabledFoundation = false, bool standalone = false, bool automatic = false,
        bool localAutomatic = false)
        : MonicaTestApplicationFactory<SeederActivationTests>
    {
        public new async Task<MonicaTestApplication> CreateAsync(
            Action<ISeamReplacementBuilder>? configureScenario = null, CancellationToken cancellationToken = default)
        {
            Assembly? ResolveFixture(object? sender, ResolveEventArgs arguments) =>
                arguments.Name == ScenarioAssembly.FullName ? ScenarioAssembly : null;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFixture;
            try { return await base.CreateAsync(configureScenario, cancellationToken); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= ResolveFixture; }
        }

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => [ScenarioAssembly];
        protected override void ConfigureHost(WebApplicationBuilder builder) => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Application:AutonomousWork"] = "false" });
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            if (!standalone)
                builder.AddModule<ActivationModule, ActivationOptions>(options =>
                {
                    options.DisabledFoundation = disabledFoundation;
                    options.Automatic = automatic;
                    options.LocalAutomatic = localAutomatic;
                });
            builder.AddSeeder();
        }
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton<Probe>();
        }
    }

    public sealed class ActivationModule : MonicaModule<ActivationOptions>, IApplicationCompositionModule
    {
        public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) =>
            Application.SetTypeActivationPolicy(new Policy(Option.DisabledFoundation, Option.Automatic, Option.LocalAutomatic));
        public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) { }
    }

    public sealed class ActivationOptions : ModuleOptions<ActivationModule>
    {
        public bool DisabledFoundation { get; set; }
        public bool Automatic { get; set; }
        public bool LocalAutomatic { get; set; }
    }

    private sealed class Policy(bool disabledFoundation, bool automatic, bool localAutomatic) : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) =>
            kind != TypeActivationKind.Seeder ? ActivationMode.Automatic :
            disabledFoundation && implementationType.Name == "Foundation" ? ActivationMode.Disabled :
            localAutomatic && implementationType.Name == "LocalInitialization" ? ActivationMode.Automatic :
            automatic ? ActivationMode.Automatic : ActivationMode.Manual;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }

    public sealed class Probe
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public bool FailTarget { get; set; }
        public bool BlockTarget { get; set; }
        public TaskCompletionSource TargetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTarget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task InvokeAsync(string name, CancellationToken cancellationToken)
        {
            Calls.Enqueue(name);
            if (name != "Target") return;
            if (FailTarget) throw new InvalidOperationException("Synthetic target failure.");
            if (BlockTarget)
            {
                TargetStarted.TrySetResult();
                await ReleaseTarget.Task.WaitAsync(cancellationToken);
            }
        }
    }

    public abstract class ScenarioSeeder(Probe probe) : ISeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken) => probe.InvokeAsync(GetType().Name, cancellationToken);
    }

    private static Assembly CreateAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Test.Seeder.ActivationFixtures"), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Main");
        Type Create(string name, Type? dependency = null, bool processLocal = false)
        {
            var type = module.DefineType($"Scenario.Seeders.{name}", TypeAttributes.Public | TypeAttributes.Sealed, typeof(ScenarioSeeder));
            var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(Probe)]);
            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, typeof(ScenarioSeeder).GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(Probe)], null)!);
            il.Emit(OpCodes.Ret);
            if (dependency is not null)
                type.SetCustomAttribute(new CustomAttributeBuilder(typeof(SeederDependsOnAttribute<>).MakeGenericType(dependency)
                    .GetConstructor(Type.EmptyTypes)!, []));
            if (processLocal)
                type.SetCustomAttribute(new CustomAttributeBuilder(typeof(ProcessLocalAttribute).GetConstructor(Type.EmptyTypes)!, []));
            return type.CreateType()!;
        }
        var foundation = Create("Foundation");
        Create("Target", foundation);
        Create("Independent");
        Create("LocalIndependent", processLocal: true);
        return Create("LocalInitialization", foundation, processLocal: true).Assembly;
    }
}
