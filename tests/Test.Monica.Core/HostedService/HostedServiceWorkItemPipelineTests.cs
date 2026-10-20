using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Execution;
using Monica.Core.HostedService;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.ObservableInstance.Services;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Core.HostedService;

public sealed class HostedServiceWorkItemPipelineTests
{
    [Fact]
    public async Task ExecuteWorkItemAsync_WhenResolvedThroughInterface_ShouldUseConcreteMethodAndDisposeScope()
    {
        await using var application = await new InterfaceWorkItemFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var capture = application.Services.GetRequiredService<WorkItemCapture>();

        await application.Services.GetRequiredService<InterfaceWorkItemHostedService>()
            .RunUntypedAsync(cancellation.Token);

        AssertInterfaceExecution<InterfaceUntypedWorkItem>(capture, typeof(IHostedServiceWorkItem), cancellation.Token);
    }

    [Fact]
    public async Task ExecuteWorkItemAsync_WhenTypedWorkItemUsesInterface_ShouldPreserveResultAndConcretePolicy()
    {
        await using var application = await new InterfaceWorkItemFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var capture = application.Services.GetRequiredService<WorkItemCapture>();

        var result = await application.Services.GetRequiredService<InterfaceWorkItemHostedService>()
            .RunTypedAsync(new WorkItemInput("interface-result"), cancellation.Token);

        result.Should().Be("interface-result");
        AssertInterfaceExecution<InterfaceTypedWorkItem>(
            capture, typeof(IHostedServiceWorkItem<WorkItemInput, string>), cancellation.Token);
    }

    private static void AssertInterfaceExecution<TImplementation>(
        WorkItemCapture capture, Type contract, CancellationToken cancellationToken)
    {
        capture.Descriptor.Should().NotBeNull();
        capture.Descriptor!.ComponentType.Should().Be(typeof(TImplementation));
        capture.Descriptor.ContractType.Should().Be(contract);
        capture.Descriptor.EntryMethod!.DeclaringType.Should().Be(typeof(TImplementation));
        capture.Descriptor.Point.Should().Be(HostedServiceExecutionPoints.WorkItem);
        capture.Descriptor.TransactionMode.Should().Be(ExecutionTransactionMode.None);
        capture.Target.Should().BeOfType<TImplementation>();
        capture.PipelineScopeIdentity.Should().NotBe(Guid.Empty);
        capture.PipelineScopeIdentity.Should().Be(capture.WorkItemScopeIdentity);
        capture.PipelineToken.Should().Be(cancellationToken);
        capture.WorkItemToken.Should().Be(cancellationToken);
        capture.Feature!.ServiceName.Should().Be(nameof(InterfaceWorkItemHostedService));
        capture.WorkItemDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteWorkItemAsync_ShouldResolveWorkerAndPipelineFromOneDisposedScope()
    {
        var capture = new WorkItemCapture();
        await using var provider = new ServiceCollection()
            .AddSingleton(capture)
            .AddScoped<ScopeIdentity>()
            .AddScoped<ScopedWorkItem>()
            .AddScoped<IExecutionPipeline, RecordingPipeline>()
            .BuildServiceProvider();
        var service = new TestHostedService(
            new ObservableInstanceRegistry(Options.Create(new ModuleObservableInstanceOption())),
            Options.Create(new ModuleHostedServiceOption()),
            provider.GetRequiredService<IServiceScopeFactory>());

        var result = await service.RunWorkItemAsync(
            new WorkItemInput("value"),
            TestContext.Current.CancellationToken);

        result.Should().Be("value");
        capture.PipelineScopeIdentity.Should().Be(capture.WorkItemScopeIdentity);
        capture.Point.Should().Be(HostedServiceExecutionPoints.WorkItem);
        capture.TransactionMode.Should().Be(ExecutionTransactionMode.Automatic);
        capture.Feature.Should().NotBeNull();
        capture.Feature!.ServiceName.Should().Be(nameof(TestHostedService));
        capture.WorkItemDisposed.Should().BeTrue();
    }

    private sealed class TestHostedService(
        ObservableInstanceRegistry observableInstanceRegistry,
        IOptions<ModuleHostedServiceOption> options,
        IServiceScopeFactory serviceScopeFactory)
        : MoHostedService(
            observableInstanceRegistry,
            options,
            serviceScopeFactory,
            NullLogger<TestHostedService>.Instance)
    {
        public Task<string> RunWorkItemAsync(
            WorkItemInput input,
            CancellationToken cancellationToken)
        {
            return ExecuteWorkItemAsync<ScopedWorkItem, WorkItemInput, string>(
                input,
                cancellationToken);
        }
    }

    private sealed record WorkItemInput(string Value);

    private sealed class ScopeIdentity
    {
        public Guid Value { get; } = Guid.NewGuid();
    }

    private sealed class WorkItemCapture
    {
        public Guid PipelineScopeIdentity { get; set; }

        public Guid WorkItemScopeIdentity { get; set; }

        public ExecutionPoint? Point { get; set; }

        public ExecutionTransactionMode TransactionMode { get; set; }

        public HostedServiceExecutionFeature? Feature { get; set; }

        public bool WorkItemDisposed { get; set; }

        public ExecutionDescriptor? Descriptor { get; set; }

        public object? Target { get; set; }

        public CancellationToken PipelineToken { get; set; }

        public CancellationToken WorkItemToken { get; set; }
    }

    private sealed class InterfaceWorkItemFactory : MonicaTestApplicationFactory<IHostedServiceWorkItem>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddHostedService();
            builder.AddExecutionPipeline()
                .AddBehavior<CaptureWorkItemBehavior<ExecutionUnit, ExecutionUnit>>(
                    descriptorFilter: descriptor => descriptor.Point == HostedServiceExecutionPoints.WorkItem,
                    lifetime: ServiceLifetime.Scoped)
                .AddBehavior<CaptureWorkItemBehavior<WorkItemInput, string>>(
                    descriptorFilter: descriptor => descriptor.Point == HostedServiceExecutionPoints.WorkItem,
                    lifetime: ServiceLifetime.Scoped);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton<WorkItemCapture>();
            services.AddScoped<ScopeIdentity>();
            services.AddScoped<IUntypedWorkItem, InterfaceUntypedWorkItem>();
            services.AddScoped<ITypedWorkItem, InterfaceTypedWorkItem>();
            services.AddSingleton<InterfaceWorkItemHostedService>();
            services.AddHostedService(provider => provider.GetRequiredService<InterfaceWorkItemHostedService>());
        }
    }

    private sealed class InterfaceWorkItemHostedService(
        IObservableInstanceRegistry observableInstanceRegistry,
        IOptions<ModuleHostedServiceOption> options,
        IServiceScopeFactory serviceScopeFactory)
        : MoHostedService(observableInstanceRegistry, options, serviceScopeFactory,
            NullLogger<InterfaceWorkItemHostedService>.Instance)
    {
        public Task RunUntypedAsync(CancellationToken cancellationToken) =>
            ExecuteWorkItemAsync<IUntypedWorkItem>(cancellationToken);

        public Task<string> RunTypedAsync(WorkItemInput input, CancellationToken cancellationToken) =>
            ExecuteWorkItemAsync<ITypedWorkItem, WorkItemInput, string>(input, cancellationToken);
    }

    private sealed class CaptureWorkItemBehavior<TInput, TResult>(ScopeIdentity identity, WorkItemCapture capture)
        : IExecutionBehavior<TInput, TResult>
    {
        public Task<TResult> ExecuteAsync(ExecutionContext<TInput> context, ExecutionDelegate<TResult> next)
        {
            capture.Descriptor = context.Descriptor;
            capture.Target = context.Target;
            capture.PipelineScopeIdentity = identity.Value;
            capture.PipelineToken = context.CancellationToken;
            capture.Feature = context.Features.GetRequired<HostedServiceExecutionFeature>();
            return next();
        }
    }

    private interface IUntypedWorkItem : IHostedServiceWorkItem;

    private interface ITypedWorkItem : IHostedServiceWorkItem<WorkItemInput, string>;

    private sealed class InterfaceUntypedWorkItem(ScopeIdentity identity, WorkItemCapture capture)
        : IUntypedWorkItem, IAsyncDisposable
    {
        [ExecutionTransaction(ExecutionTransactionMode.None)]
        Task IHostedServiceWorkItem.ExecuteAsync(CancellationToken cancellationToken)
        {
            capture.WorkItemScopeIdentity = identity.Value;
            capture.WorkItemToken = cancellationToken;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            capture.WorkItemDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InterfaceTypedWorkItem(ScopeIdentity identity, WorkItemCapture capture)
        : ITypedWorkItem, IAsyncDisposable
    {
        [ExecutionTransaction(ExecutionTransactionMode.None)]
        Task<string> IHostedServiceWorkItem<WorkItemInput, string>.ExecuteAsync(
            WorkItemInput input, CancellationToken cancellationToken)
        {
            capture.WorkItemScopeIdentity = identity.Value;
            capture.WorkItemToken = cancellationToken;
            return Task.FromResult(input.Value);
        }

        public ValueTask DisposeAsync()
        {
            capture.WorkItemDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPipeline(
        ScopeIdentity identity,
        WorkItemCapture capture) : IExecutionPipeline
    {
        public Task<TResult> ExecuteAsync<TInput, TResult>(
            ExecutionDescriptor descriptor,
            TInput input,
            object? target,
            ExecutionDelegate<TResult> terminal,
            CancellationToken cancellationToken = default,
            ExecutionFeatureCollection? features = null)
        {
            Record(descriptor, features);
            return terminal();
        }

        public Task ExecuteAsync<TInput>(
            ExecutionDescriptor descriptor,
            TInput input,
            object? target,
            Func<Task> terminal,
            CancellationToken cancellationToken = default,
            ExecutionFeatureCollection? features = null)
        {
            Record(descriptor, features);
            return terminal();
        }

        private void Record(ExecutionDescriptor descriptor, ExecutionFeatureCollection? features)
        {
            capture.PipelineScopeIdentity = identity.Value;
            capture.Point = descriptor.Point;
            capture.TransactionMode = descriptor.TransactionMode;
            capture.Feature = features?.GetRequired<HostedServiceExecutionFeature>();
        }
    }

    private sealed class ScopedWorkItem(
        ScopeIdentity identity,
        WorkItemCapture capture) :
        IHostedServiceWorkItem<WorkItemInput, string>,
        IDisposable
    {
        public Task<string> ExecuteAsync(
            WorkItemInput input,
            CancellationToken cancellationToken)
        {
            capture.WorkItemScopeIdentity = identity.Value;
            return Task.FromResult(input.Value);
        }

        public void Dispose()
        {
            capture.WorkItemDisposed = true;
        }
    }
}
