using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Monica.Repository.UnitOfWork.Abstractions;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class UnitOfWorkPipelineIntegrationTests
{
    [Fact]
    public async Task Mediator_WhenHandlerDeclaresNone_ShouldSkipTransactionAndRetainEnclosingSession()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.AddUnitOfWork();
            monica.AddMediator();
        });
        builder.Services.AddScoped<IRequestHandler<ReadQuery, bool>, ReadQueryHandler>();
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        Assert.False(await mediator.Send(new ReadQuery(), TestContext.Current.CancellationToken));
        await manager.RunAsync(async () =>
            Assert.True(await mediator.Send(new ReadQuery(), TestContext.Current.CancellationToken)),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    public sealed record ReadQuery : IRequest<bool>;

    private sealed class ReadQueryHandler(IUnitOfWorkManager manager) : IRequestHandler<ReadQuery, bool>
    {
        [ExecutionTransaction(ExecutionTransactionMode.None)]
        public Task<bool> Handle(ReadQuery request, CancellationToken cancellationToken)
            => Task.FromResult(manager.Current is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ShouldApplyUnitOfWorkOnlyToAutomaticBoundaries(
        bool registerTransitively)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            if (registerTransitively)
            {
                monica.AddModule<TransitiveUnitOfWorkModule, TransitiveUnitOfWorkModuleOption>();
            }
            else
            {
                monica.AddUnitOfWork();
            }
        });
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IExecutionPipeline>();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var automaticSawAmbientScope = false;
        var noneSawAmbientScope = false;

        await pipeline.ExecuteAsync(
            CreateDescriptor(ExecutionTransactionMode.Automatic),
            ExecutionUnit.Value,
            target: null,
            () =>
            {
                automaticSawAmbientScope = manager.Current is not null;
                return Task.CompletedTask;
            },
            cancellationToken);

        manager.Current.Should().BeNull();

        await pipeline.ExecuteAsync(
            CreateDescriptor(ExecutionTransactionMode.None),
            ExecutionUnit.Value,
            target: null,
            () =>
            {
                noneSawAmbientScope = manager.Current is not null;
                return Task.CompletedTask;
            },
            cancellationToken);

        automaticSawAmbientScope.Should().BeTrue();
        noneSawAmbientScope.Should().BeFalse();
        manager.Current.Should().BeNull();
    }

    private static ExecutionDescriptor CreateDescriptor(ExecutionTransactionMode transactionMode)
    {
        return ExecutionDescriptor.ForMethod<ExecutionUnit, ExecutionUnit>(
            new ExecutionPoint("test.unit-of-work-selection"),
            typeof(UnitOfWorkPipelineIntegrationTests),
            entryMethod: null,
            isBusinessOperation: true,
            transactionMode: transactionMode);
    }

    public sealed class TransitiveUnitOfWorkModule : MonicaModule<TransitiveUnitOfWorkModuleOption>
    {
        public override void Describe(ModuleDescriptor module)
        {
            module.Require<ModuleUnitOfWork>();
        }
    }

    public sealed class TransitiveUnitOfWorkModuleOption : ModuleOptions<TransitiveUnitOfWorkModule>;
}
