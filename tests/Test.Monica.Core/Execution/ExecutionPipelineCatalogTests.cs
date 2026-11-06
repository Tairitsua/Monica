using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Execution;
using Monica.Core.Execution.Abstractions;
using Monica.Core.Execution.Facades;
using Monica.Core.Execution.Models;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Tool.Extensions;
using Xunit;

namespace Test.Monica.Core.Execution;

[Collection(BlockingConcurrencyCollection.Name)]
public sealed class ExecutionPipelineCatalogTests
{
    private static readonly ExecutionPoint CATALOG_POINT = new("test.catalog");

    [Fact]
    public async Task GetSnapshot_WhenHostAndModuleBehaviorsExecute_ShouldReportExactOrderAndSources()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.AddExecutionPipeline()
                .AddBehavior<HostCatalogBehavior>(order: 200, lifetime: ServiceLifetime.Scoped);
            monica.AddModule<CatalogContributorModule, CatalogContributorModuleOption>();
        });
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var catalog = host.Services.GetRequiredService<IExecutionPipelineCatalog>();
        var descriptor = CreateDescriptor();

        catalog.GetSnapshot().Plans.Should().BeEmpty();
        await ExecuteAsync(scope.ServiceProvider.GetRequiredService<IExecutionPipeline>(), descriptor);

        var snapshot = catalog.GetSnapshot();
        snapshot.Registrations.Select(static registration => registration.BehaviorType.FullName)
            .Should().Equal(
                typeof(ModuleCatalogBehavior).GetCleanFullName(),
                typeof(HostCatalogBehavior).GetCleanFullName());
        snapshot.Registrations[0].SourceModuleKey.Should().BeNull();
        snapshot.Registrations[1].SourceModuleKey.Should().BeNull();
        snapshot.Registrations[1].Lifetime.Should().Be(ServiceLifetime.Scoped);

        var plan = snapshot.Plans.Should().ContainSingle().Subject;
        plan.Status.Should().Be(ExecutionPipelinePlanStatus.Ready);
        plan.Error.Should().BeNull();
        plan.Behaviors.Select(static behavior => behavior.Position).Should().Equal(1, 2);
        plan.Behaviors.Select(static behavior => behavior.ResolvedType.FullName)
            .Should().Equal(
                typeof(ModuleCatalogBehavior).GetCleanFullName(),
                typeof(HostCatalogBehavior).GetCleanFullName());
    }

    [Fact]
    public async Task InspectPlan_WhenPlanIsReadRepeatedly_ShouldNotReevaluateFiltersOrResolveBehaviors()
    {
        var filterInvocations = 0;
        var activationCounter = new BehaviorActivationCounter();
        using var host = BuildHost(
            registration => registration.AddBehavior<CatalogBehavior>(descriptorFilter: _ =>
            {
                Interlocked.Increment(ref filterInvocations);
                return true;
            }),
            services => services.AddSingleton(activationCounter));
        var catalog = host.Services.GetRequiredService<IExecutionPipelineCatalog>();
        var descriptor = CreateDescriptor();

        var inspected = catalog.InspectPlan(descriptor);
        var firstSnapshot = catalog.GetSnapshot();
        var secondSnapshot = catalog.GetSnapshot();

        inspected.Status.Should().Be(ExecutionPipelinePlanStatus.Ready);
        firstSnapshot.Plans.Should().ContainSingle();
        secondSnapshot.Plans.Should().ContainSingle();
        filterInvocations.Should().Be(1);
        activationCounter.Count.Should().Be(0);

        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();
        await ExecuteAsync(firstScope.ServiceProvider.GetRequiredService<IExecutionPipeline>(), descriptor);
        await ExecuteAsync(secondScope.ServiceProvider.GetRequiredService<IExecutionPipeline>(), descriptor);

        filterInvocations.Should().Be(1);
        activationCounter.Count.Should().Be(2);
    }

    [Fact]
    public async Task InspectPlan_WhenDescriptorFilterFails_ShouldCacheFaultForExecution()
    {
        var filterInvocations = 0;
        var expected = new CatalogPlanException("filter failed");
        using var host = BuildHost(registration => registration.AddBehavior<CatalogBehavior>(descriptorFilter: _ =>
        {
            Interlocked.Increment(ref filterInvocations);
            throw expected;
        }));
        var catalog = host.Services.GetRequiredService<IExecutionPipelineCatalog>();
        var descriptor = CreateDescriptor();

        var inspected = catalog.InspectPlan(descriptor);

        inspected.Status.Should().Be(ExecutionPipelinePlanStatus.Faulted);
        inspected.Behaviors.Should().BeEmpty();
        inspected.Error.Should().NotBeNull();
        inspected.Error!.ExceptionType.Should().Be(typeof(CatalogPlanException).FullName);
        inspected.Error.Message.Should().Contain("filter failed");

        using var scope = host.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IExecutionPipeline>();
        Func<Task> firstExecution = async () => await ExecuteAsync(pipeline, descriptor);
        Func<Task> secondExecution = async () => await ExecuteAsync(pipeline, descriptor);

        (await firstExecution.Should().ThrowAsync<CatalogPlanException>()).Which.Should().BeSameAs(expected);
        (await secondExecution.Should().ThrowAsync<CatalogPlanException>()).Which.Should().BeSameAs(expected);
        filterInvocations.Should().Be(1);
    }

    [Fact]
    public void InspectPlan_WhenPoliciesDiffer_ShouldUseDistinctPlanKeysForTheSameOperation()
    {
        using var host = BuildHost(_ => { });
        var catalog = host.Services.GetRequiredService<IExecutionPipelineCatalog>();
        var businessDescriptor = CreateDescriptor(
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var infrastructureDescriptor = CreateDescriptor(
            isBusinessOperation: false,
            transactionMode: ExecutionTransactionMode.None);

        var businessPlan = catalog.InspectPlan(businessDescriptor);
        var infrastructurePlan = catalog.InspectPlan(infrastructureDescriptor);

        businessPlan.Descriptor.Id.Should().Be(infrastructurePlan.Descriptor.Id);
        businessPlan.Id.Should().NotBe(infrastructurePlan.Id);
        businessPlan.Status.Should().Be(ExecutionPipelinePlanStatus.Ready);
        businessPlan.Behaviors.Should().BeEmpty();
        catalog.GetSnapshot().Plans.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetPlan_WhenConcurrentExecutionsMaterializeOneDescriptor_ShouldEvaluateFilterOnce()
    {
        var filterInvocations = 0;
        var filterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFilter = new ManualResetEventSlim();
        using var host = BuildHost(registration => registration.AddBehavior<CatalogBehavior>(descriptorFilter: _ =>
        {
            Interlocked.Increment(ref filterInvocations);
            filterStarted.TrySetResult();
            releaseFilter.Wait(TestContext.Current.CancellationToken);
            return true;
        }));
        var catalog = host.Services.GetRequiredService<IExecutionPipelineCatalog>();
        var descriptor = CreateDescriptor();
        var executions = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                using var scope = host.Services.CreateScope();
                await ExecuteAsync(scope.ServiceProvider.GetRequiredService<IExecutionPipeline>(), descriptor);
            }, TestContext.Current.CancellationToken))
            .ToArray();

        await filterStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var buildingPlan = catalog.GetSnapshot().Plans.Should().ContainSingle().Subject;
        buildingPlan.Status.Should().Be(ExecutionPipelinePlanStatus.Building);

        releaseFilter.Set();
        await Task.WhenAll(executions);

        filterInvocations.Should().Be(1);
        catalog.GetSnapshot().Plans.Should().ContainSingle()
            .Which.Status.Should().Be(ExecutionPipelinePlanStatus.Ready);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenCatalogIsRegistered_ShouldReturnSuccessfulFacadeResult()
    {
        using var host = BuildHost(_ => { });
        var facade = host.Services.GetRequiredService<ExecutionPipelineCatalogFacade>();

        var result = await facade.GetSnapshotAsync();

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.Plans.Should().BeEmpty();
    }

    [Fact]
    public void InspectPlan_WhenOpenGenericBehaviorApplies_ShouldReportRegistrationAndClosedTypeMetadata()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica => monica.AddModule<
            OpenCatalogContributorModule,
            OpenCatalogContributorModuleOption>());
        using var host = builder.Build();
        var catalog = host.Services.GetRequiredService<IExecutionPipelineCatalog>();

        var plan = catalog.InspectPlan(CreateDescriptor());

        var registration = catalog.GetSnapshot().Registrations.Should().ContainSingle().Subject;
        registration.BehaviorType.Diagnostics.AssemblyQualifiedName
            .Should().Be(typeof(OpenCatalogBehavior<,>).AssemblyQualifiedName);
        registration.BehaviorType.FullName.Should().Be(typeof(OpenCatalogBehavior<,>).GetCleanFullName());
        registration.BehaviorType.FullName.Should().NotContain("Version=");
        registration.Order.Should().Be(321);
        registration.Lifetime.Should().Be(ServiceLifetime.Singleton);
        registration.SourceModuleKey.Should().BeNull();
        registration.IsOpenGeneric.Should().BeTrue();
        registration.HasDescriptorFilter.Should().BeTrue();

        var applied = plan.Behaviors.Should().ContainSingle().Subject;
        applied.RegisteredType.Diagnostics.AssemblyQualifiedName
            .Should().Be(typeof(OpenCatalogBehavior<,>).AssemblyQualifiedName);
        applied.ResolvedType.Diagnostics.AssemblyQualifiedName.Should().Be(
            typeof(OpenCatalogBehavior<CatalogRequest, string>).AssemblyQualifiedName);
        applied.ResolvedType.FullName.Should().Be(
            typeof(OpenCatalogBehavior<CatalogRequest, string>).GetCleanFullName());
        applied.ResolvedType.FullName.Should().NotContain("Version=");
        applied.Order.Should().Be(registration.Order);
        applied.Lifetime.Should().Be(registration.Lifetime);
        applied.SourceModuleKey.Should().Be(registration.SourceModuleKey);
    }

    private static IHost BuildHost(
        Action<ModuleRegistration<ModuleExecutionPipeline, ModuleExecutionPipelineOption>> configure,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder();
        configureServices?.Invoke(builder.Services);
        builder.AddMonica(monica => configure(monica.AddExecutionPipeline()));
        return builder.Build();
    }

    private static ExecutionDescriptor CreateDescriptor(
        bool isBusinessOperation = true,
        ExecutionTransactionMode transactionMode = ExecutionTransactionMode.Automatic)
    {
        return ExecutionDescriptor.ForMethod<CatalogRequest, string>(
            CATALOG_POINT,
            typeof(ExecutionPipelineCatalogTests),
            entryMethod: null,
            isBusinessOperation,
            transactionMode);
    }

    private static Task<string> ExecuteAsync(IExecutionPipeline pipeline, ExecutionDescriptor descriptor)
    {
        return pipeline.ExecuteAsync(
            descriptor,
            new CatalogRequest(),
            target: null,
            static () => Task.FromResult("result"),
            TestContext.Current.CancellationToken);
    }

    public sealed record CatalogRequest;

    private sealed class BehaviorActivationCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    private sealed class CatalogBehavior : IExecutionBehavior<CatalogRequest, string>
    {
        public CatalogBehavior(BehaviorActivationCounter? counter = null)
        {
            counter?.Increment();
        }

        public Task<string> ExecuteAsync(
            ExecutionContext<CatalogRequest> context,
            ExecutionDelegate<string> next) => next();
    }

    public sealed class HostCatalogBehavior : IExecutionBehavior<CatalogRequest, string>
    {
        public Task<string> ExecuteAsync(
            ExecutionContext<CatalogRequest> context,
            ExecutionDelegate<string> next) => next();
    }

    public sealed class ModuleCatalogBehavior : IExecutionBehavior<CatalogRequest, string>
    {
        public Task<string> ExecuteAsync(
            ExecutionContext<CatalogRequest> context,
            ExecutionDelegate<string> next) => next();
    }

    public sealed class OpenCatalogBehavior<TInput, TResult> : IExecutionBehavior<TInput, TResult>
    {
        public Task<TResult> ExecuteAsync(
            ExecutionContext<TInput> context,
            ExecutionDelegate<TResult> next) => next();
    }

    private sealed class CatalogPlanException(string message) : Exception(message);
}

internal sealed class CatalogContributorModule : MonicaModule<CatalogContributorModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(options =>
            options.AddBehavior(
                typeof(ExecutionPipelineCatalogTests.ModuleCatalogBehavior),
                order: 100));
    }
}

internal sealed class CatalogContributorModuleOption : ModuleOptions<CatalogContributorModule>;

internal sealed class OpenCatalogContributorModule : MonicaModule<OpenCatalogContributorModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(options =>
            options.AddBehavior(
                typeof(ExecutionPipelineCatalogTests.OpenCatalogBehavior<,>),
                order: 321,
                descriptorFilter: static _ => true,
                lifetime: ServiceLifetime.Singleton));
    }
}

internal sealed class OpenCatalogContributorModuleOption : ModuleOptions<OpenCatalogContributorModule>;
