using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Framework.ChainTracing.Providers.AspNetCore;
using Monica.Framework.ChainTracing.Services;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTracingResultMetadataFilterTests
{
    [Fact]
    public async Task OnActionExecuted_WhenControllerFails_ShouldProjectItsExceptionAfterTheScopeCompletes()
    {
        await using var application = await new ChainTracingTestApplicationFactory().CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        using var scope = application.Services.CreateScope();
        var tracing = scope.ServiceProvider.GetRequiredService<IChainTracing>();
        var mvcOptions = scope.ServiceProvider.GetRequiredService<IOptions<MvcOptions>>().Value;
        // MVC sorts the registered factory descriptors before it creates their filter instances. Sorting
        // only the instantiated filters would miss a missing Order on the module's TypeFilterAttribute.
        var descriptors = mvcOptions.Filters.OfType<TypeFilterAttribute>()
            .Where(filter => filter.ImplementationType == typeof(ChainTracingControllerActionFilter)
                || filter.ImplementationType == typeof(ChainTracingResultMetadataActionFilter))
            .Select(filter => new FilterDescriptor(filter, FilterScope.Global))
            .OrderBy(descriptor => descriptor.Order).ThenBy(descriptor => descriptor.Scope).ToArray();
        descriptors.Should().HaveCount(2);
        ((TypeFilterAttribute)descriptors[0].Filter).ImplementationType.Should()
            .Be(typeof(ChainTracingResultMetadataActionFilter));
        descriptors[0].Order.Should().BeLessThan(descriptors[1].Order);
        var filters = descriptors.Select(descriptor => (IActionFilter)
            ((IFilterFactory)descriptor.Filter).CreateInstance(scope.ServiceProvider)).ToArray();
        var context = ExecutedContext(Res.Fail("Safe failure"));
        context.ActionDescriptor.DisplayName = "Action";
        var executing = new ActionExecutingContext(context, [], new Dictionary<string, object?>(), context.Controller);
        foreach (var filter in filters) filter.OnActionExecuting(executing);
        context.Exception = new InvalidOperationException("controller failure");
        foreach (var filter in filters.Reverse()) filter.OnActionExecuted(context);

        var root = tracing.GetCurrentChain()!.Root!;
        root.EndTime.Should().NotBeNull();
        root.ExceptionId.Should().NotBeNullOrEmpty();
        var response = ((ObjectResult)context.Result!).Value;
        var metadata = JsonSerializer.SerializeToElement(response, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("metadata");
        metadata.GetProperty("chain").GetProperty("exceptionId").GetString().Should().Be(root.ExceptionId);
        metadata.GetProperty("diagnostics").GetProperty("exceptions").EnumerateArray()
            .Should().ContainSingle(entry => entry.GetProperty("id").GetString() == root.ExceptionId
                && entry.GetProperty("message").GetString() == "controller failure");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnActionExecuted_ShouldAttachChainOnlyForDiagnosticHosts(bool exposeDiagnostics)
    {
        var tracing = CreateTracingWithDatabaseNode();
        var filter = new ChainTracingResultMetadataActionFilter(tracing,
            new ChainResultMetadataAttacher(Options.Create(
                new ModuleResultEnvelopeOption { ExposeDiagnosticDetails = exposeDiagnostics })));
        var response = Res.Fail("Safe failure");
        var context = ExecutedContext(response);

        filter.OnActionExecuted(context);

        var json = JsonSerializer.Serialize(response);
        json.Should().Contain("traceId");
        if (exposeDiagnostics)
        {
            json.Should().Contain("chain");
            json.Should().Contain("SELECT 1");
        }
        else
        {
            json.Should().NotContain("chain");
            json.Should().NotContain("SELECT 1");
        }
    }

    private static AsyncLocalChainTracingService CreateTracingWithDatabaseNode()
    {
        var tracing = new AsyncLocalChainTracingService(
            Options.Create(new ModuleChainTracingOption()),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));
        tracing.BeginTrace("SELECT 1", null, null, EChainTracingType.Database);
        return tracing;
    }

    private static ActionExecutedContext ExecutedContext(Res response)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutedContext(actionContext, [], new object())
        {
            Result = new ObjectResult(response)
        };
    }

    private sealed class ChainTracingTestApplicationFactory : MonicaTestApplicationFactory<ModuleChainTracing>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddResultEnvelope(options => options.ExposeDiagnosticDetails = true);
            builder.AddChainTracing().UseControllerTracing().AttachControllerTraceMetadata();
        }
    }
}
