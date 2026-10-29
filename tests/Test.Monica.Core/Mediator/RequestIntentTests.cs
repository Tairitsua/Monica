using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Mediator;

public sealed class RequestIntentTests
{
    [Fact]
    public async Task Send_WhenRequestDeclaresBothIntents_ShouldRejectBeforeHandler()
    {
        using var host = BuildHost<ConflictingRequest>();
        using var scope = host.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<ConflictingRequest>(scope));
    }

    [Fact]
    public async Task Send_WhenRequestDeclaresQuery_ShouldSkipAutomaticTransaction()
    {
        using var host = BuildHost<ReadProjectionRequest>();
        using var scope = host.Services.CreateScope();

        (await SendAsync<ReadProjectionRequest>(scope))
            .Should().Be(nameof(ExecutionTransactionMode.None));
    }

    [Fact]
    public async Task Send_WhenQueryExplicitlyRequestsTransaction_ShouldHonorOverride()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services
            .AddTransient<IRequestHandler<ReadProjectionRequest, string>, ConsistentReadHandler<ReadProjectionRequest>>();
        builder.AddMonica(monica =>
        {
            monica.AddMediator();
            monica.AddExecutionPipeline().AddBehavior<TransactionModeBehavior<ReadProjectionRequest>>();
        });

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        (await SendAsync<ReadProjectionRequest>(scope))
            .Should().Be(nameof(ExecutionTransactionMode.Automatic));
    }

    [Fact]
    public async Task Send_WhenRequestIsUnclassified_ShouldDefaultToAutomatic()
    {
        using var host = BuildHost<PlainRequest>();
        using var scope = host.Services.CreateScope();

        (await SendAsync<PlainRequest>(scope))
            .Should().Be(nameof(ExecutionTransactionMode.Automatic));
    }

    private static IHost BuildHost<TRequest>() where TRequest : IRequest<string>, new()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTransient<IRequestHandler<TRequest, string>, NoOpHandler<TRequest>>();
        builder.AddMonica(monica =>
        {
            monica.AddMediator();
            monica.AddExecutionPipeline().AddBehavior<TransactionModeBehavior<TRequest>>();
        });
        return builder.Build();
    }

    private static Task<string> SendAsync<TRequest>(IServiceScope scope) where TRequest : IRequest<string>, new()
        => scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new TRequest(), TestContext.Current.CancellationToken);

    private sealed record ReadProjectionRequest : IRequest<string>, IQueryRequest;

    private sealed record PlainRequest : IRequest<string>;

    private sealed record ConflictingRequest : IRequest<string>, IQueryRequest, ICommandRequest;

    private sealed class NoOpHandler<TRequest> : IRequestHandler<TRequest, string>
        where TRequest : IRequest<string>
    {
        public Task<string> Handle(TRequest request, CancellationToken cancellationToken) => Task.FromResult("handled");
    }

    private sealed class ConsistentReadHandler<TRequest> : IRequestHandler<TRequest, string>
        where TRequest : IRequest<string>
    {
        [ExecutionTransaction(ExecutionTransactionMode.Automatic)]
        public Task<string> Handle(TRequest request, CancellationToken cancellationToken) => Task.FromResult("handled");
    }

    private sealed class TransactionModeBehavior<TRequest> : IExecutionBehavior<TRequest, string>
        where TRequest : IRequest<string>
    {
        public Task<string> ExecuteAsync(
            ExecutionContext<TRequest> context,
            ExecutionDelegate<string> next)
            => Task.FromResult(context.Descriptor.TransactionMode.ToString());
    }
}
