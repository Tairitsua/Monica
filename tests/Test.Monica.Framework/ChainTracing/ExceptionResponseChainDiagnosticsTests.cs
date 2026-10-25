using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Core.Results;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Providers.AspNetCore;
using Monica.Framework.ChainTracing.Services;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

/// <summary>
/// Verifies that exception-built responses recover the request chain through HttpContext items: the
/// exception handler runs after the pipeline unwinds, where downstream AsyncLocal mutations are gone.
/// </summary>
public sealed class ExceptionResponseChainDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Attach_ShouldRecoverChainFromHttpContextItems_LikeTheUnwindPathDoes(bool exposeDiagnostics)
    {
        // The chain is built by a separate tracing instance, simulating a downstream flow whose AsyncLocal
        // state is invisible to the exception handler.
        var downstream = CreateTracing();
        var root = downstream.BeginTrace("Action", "Controller");
        var sql = downstream.BeginTrace("SELECT 1", null, type: EChainTracingType.Database);
        downstream.EndTrace(sql);
        downstream.EndTrace(root, "Res(Ok)");
        var httpContext = new DefaultHttpContext();
        httpContext.Items[ChainTraceContext.HTTP_ITEM_KEY] = downstream.GetCurrentChain();

        // Fresh instance: no ambient chain, exactly like the unwound exception-handler flow.
        var diagnostics = new ExceptionResponseChainDiagnostics(
            CreateTracing(), CreateAttacher(exposeDiagnostics));
        var response = Res.Fail("Safe failure", ResStatus.InternalError);

        diagnostics.Attach(httpContext, response);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.Serialize(response, jsonOptions);
        json.Should().Contain("traceId");
        if (exposeDiagnostics)
        {
            json.Should().Contain("SELECT 1");
            var chain = JsonSerializer.SerializeToElement(response, jsonOptions)
                .GetProperty("metadata").GetProperty("chain");
            chain.GetProperty("timestamp").GetDateTimeOffset().Should().Be(downstream.GetCurrentChain()!.Root!.Timestamp);
            chain.GetProperty("serviceVersion").GetString().Should().Be("1.0.0+app-x-commit");
        }
        else
        {
            json.Should().NotContain("SELECT 1");
            json.Should().NotContain("chain");
        }
    }

    [Fact]
    public void Attach_WhenNoChainWasPublished_ShouldAttachNothing()
    {
        var diagnostics = new ExceptionResponseChainDiagnostics(CreateTracing(), CreateAttacher(true));
        var response = Res.Fail("Safe failure", ResStatus.InternalError);

        diagnostics.Attach(new DefaultHttpContext(), response);

        var json = JsonSerializer.Serialize(response);
        json.Should().NotContain("traceId");
        json.Should().NotContain("chain");
    }

    private static ChainResultMetadataAttacher CreateAttacher(bool exposeDiagnostics)
    {
        return new ChainResultMetadataAttacher(Options.Create(
            new ModuleResultEnvelopeOption { ExposeDiagnosticDetails = exposeDiagnostics }));
    }

    private static AsyncLocalChainTracingService CreateTracing()
    {
        return new AsyncLocalChainTracingService(
            Options.Create(new ModuleChainTracingOption { ServiceName = "app-x", ServiceVersion = "1.0.0+app-x-commit" }),
            NullLogger<AsyncLocalChainTracingService>.Instance,
            new JsonSerializerOptionsProvider(new JsonSerializerOptions(), DateTimeWireFormat.Iso8601WallClock));
    }
}
