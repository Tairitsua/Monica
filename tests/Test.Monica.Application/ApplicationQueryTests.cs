using Microsoft.Extensions.DependencyInjection;
using Monica.Application.Annotations;
using Monica.Core.Execution;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Application;

public sealed class ApplicationQueryTests
{
    [Fact]
    public async Task Send_WhenQueryUsesGetOrPost_ShouldBeReadOnlyUnlessHandlerOverrides()
    {
        await using var app = await new QueryFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = app.CreateScope(TestContext.Current.CancellationToken);
        var mediator = scope.Resolve<IMediator>();
        Assert.Equal("None", await mediator.Send(new QueryGet(), TestContext.Current.CancellationToken));
        Assert.Equal("None", await mediator.Send(new QueryPost(), TestContext.Current.CancellationToken));
        Assert.Equal("Automatic", await mediator.Send(new CommandGet(), TestContext.Current.CancellationToken));
        Assert.Equal("Automatic", await mediator.Send(new QueryOverride(), TestContext.Current.CancellationToken));
    }

    private sealed class QueryFactory : MonicaTestApplicationFactory<ApplicationQueryTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddApplication();
            builder.AddExecutionPipeline()
                .AddBehavior<TransactionProbe<QueryGet>>()
                .AddBehavior<TransactionProbe<QueryPost>>()
                .AddBehavior<TransactionProbe<CommandGet>>()
                .AddBehavior<TransactionProbe<QueryOverride>>();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddTransient<IRequestHandler<QueryGet, string>, Handler<QueryGet>>();
            services.AddTransient<IRequestHandler<QueryPost, string>, Handler<QueryPost>>();
            services.AddTransient<IRequestHandler<CommandGet, string>, Handler<CommandGet>>();
            services.AddTransient<IRequestHandler<QueryOverride, string>, OverrideHandler>();
        }
    }

    private sealed class Handler<T> : IRequestHandler<T, string> where T : IRequest<string>
    {
        public Task<string> Handle(T request, CancellationToken cancellationToken) => Task.FromResult("handled");
    }

    private sealed class OverrideHandler : IRequestHandler<QueryOverride, string>
    {
        [ExecutionTransaction(ExecutionTransactionMode.Automatic)]
        public Task<string> Handle(QueryOverride request, CancellationToken cancellationToken) => Task.FromResult("handled");
    }

    private sealed class TransactionProbe<T> : IExecutionBehavior<T, string>
    {
        public Task<string> ExecuteAsync(ExecutionContext<T> context, ExecutionDelegate<string> next)
            => Task.FromResult(context.Descriptor.TransactionMode.ToString());
    }
}

[ApiEndpoint(ApiHttpMethod.Get, "probe-get")]
public sealed record QueryGet : IRequest<string>, global::Monica.Core.Mediator.IQueryRequest;
[ApiEndpoint(ApiHttpMethod.Post, "probe")]
public sealed record QueryPost : IRequest<string>, global::Monica.Core.Mediator.IQueryRequest;
[ApiEndpoint(ApiHttpMethod.Get, "probe")]
public sealed record CommandGet : IRequest<string>, global::Monica.Core.Mediator.ICommandRequest;
[ApiEndpoint(ApiHttpMethod.Post, "probe-override")]
public sealed record QueryOverride : IRequest<string>, global::Monica.Core.Mediator.IQueryRequest;
