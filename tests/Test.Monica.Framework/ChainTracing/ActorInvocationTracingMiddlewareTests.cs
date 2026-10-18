using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Providers.Actor;
using Monica.Framework.ChainTracing.Services;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

/// <summary>
/// Verifies that the actor tracing node reflects the observed outcome: the HTTP status the actor runtime
/// produced, or a propagated exception — never an unconditional success.
/// </summary>
public sealed class ActorInvocationTracingMiddlewareTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(500, true)]
    public async Task InvokeAsync_AfterActorEndpoint_ShouldRecordOutcomeFromHttpStatus(int statusCode, bool expectFailed)
    {
        var tracing = CreateTracing();
        var httpContext = CreateActorContext();

        await InvokeAsync(tracing, httpContext, context =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        // AsyncLocal mutations do not flow back past the await; assert through the published items channel.
        var chain = httpContext.Items[ChainTraceContext.HTTP_ITEM_KEY].Should().BeOfType<ChainTraceContext>().Subject;
        var root = chain.Root!;
        root.Operation.Should().Be("Actor invocation");
        if (expectFailed)
        {
            root.IsFailed.Should().BeTrue();
            root.Result.Should().Contain($"HTTP {statusCode}");
        }
        else
        {
            root.IsFailed.Should().BeNull();
            root.Result.Should().Contain("Success");
        }
    }

    [Fact]
    public async Task InvokeAsync_WhenEndpointThrows_ShouldRecordExceptionAndRethrow()
    {
        var tracing = CreateTracing();
        var httpContext = CreateActorContext();
        var failure = new InvalidOperationException("actor exploded");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(tracing, httpContext, _ => Task.FromException(failure)));

        var chain = httpContext.Items[ChainTraceContext.HTTP_ITEM_KEY].Should().BeOfType<ChainTraceContext>().Subject;
        var root = chain.Root!;
        root.IsFailed.Should().BeTrue();
        root.Exception.Should().BeSameAs(failure);
    }

    [Fact]
    public async Task InvokeAsync_ForOtherEndpoints_ShouldNotCreateAnyChain()
    {
        var tracing = CreateTracing();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream();
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "Health checks"));

        await InvokeAsync(tracing, httpContext, _ => Task.CompletedTask);

        tracing.HasActiveChain().Should().BeFalse();
    }

    private static Task InvokeAsync(AsyncLocalChainTracingService tracing, HttpContext httpContext,
        RequestDelegate next)
    {
        var middleware = new ActorInvocationTracingMiddleware(
            new JsonSerializerOptionsProvider(new System.Text.Json.JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock),
            tracing);
        return middleware.InvokeAsync(httpContext, next);
    }

    private static DefaultHttpContext CreateActorContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream();
        httpContext.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(),
            "Dapr Actors Invoke"));
        return httpContext;
    }

    private static AsyncLocalChainTracingService CreateTracing()
    {
        return new AsyncLocalChainTracingService(
            Options.Create(new ModuleChainTracingOption { ServiceName = "actor-host" }),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new System.Text.Json.JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));
    }
}
