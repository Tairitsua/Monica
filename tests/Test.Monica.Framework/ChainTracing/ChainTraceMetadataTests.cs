using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTraceMetadataTests
{
    private const string SERVICE_VERSION = "1.2.3+abcdef0123456789";

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Attach_WhenChainCompletes_ShouldPreserveRootOriginAndRespectDiagnosticVisibility(
        bool exposeDiagnostics, bool success)
    {
        await using var application = await new ChainTraceMetadataTestApplicationFactory(exposeDiagnostics)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var tracing = application.Services.GetRequiredService<IChainTracing>();
        var serializer = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>();
        var attacher = application.Services.GetRequiredService<ChainResultMetadataAttacher>();
        var before = DateTimeOffset.UtcNow;
        var rootId = tracing.BeginTrace("Action", "Controller");
        var after = DateTimeOffset.UtcNow;
        var timestamp = tracing.GetCurrentChain()!.Root!.Timestamp!.Value;
        var childId = tracing.BeginTrace("Handle", "Handler");
        tracing.EndTrace(childId, success: success);
        tracing.EndTrace(rootId, success: success);
        var response = success ? Res.Ok() : Res.Fail("Safe failure");

        var httpContext = new DefaultHttpContext();
        attacher.Attach(tracing.GetCurrentChain(), httpContext, response);
        attacher.Attach(tracing.GetCurrentChain(), httpContext, response);

        var metadata = JsonSerializer.SerializeToElement(response, serializer.SerializerOptions)
            .GetProperty("metadata");
        metadata.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
        if (!exposeDiagnostics)
        {
            metadata.TryGetProperty("chain", out _).Should().BeFalse();
            metadata.ToString().Should().NotContain(SERVICE_VERSION);
            return;
        }

        var root = metadata.GetProperty("chain");
        root.GetProperty("service").GetString().Should().Be("test-api");
        root.GetProperty("serviceVersion").GetString().Should().Be(SERVICE_VERSION);
        root.GetProperty("timestamp").GetDateTimeOffset().Should().Be(timestamp);
        timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        timestamp.Offset.Should().Be(TimeSpan.Zero);
        root.GetProperty("timestamp").GetString().Should().EndWith("+00:00",
            "an absolute trace instant must retain its offset under wall-clock business formatting");
        var child = root.GetProperty("children")[0];
        child.TryGetProperty("timestamp", out _).Should().BeFalse();
        child.TryGetProperty("serviceVersion", out _).Should().BeFalse();
        child.TryGetProperty("service", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Attach_WhenHostIdentityIsUnset_ShouldStillExposeTimestampAndOmitEmptyVersion(string? version)
    {
        await using var application = await new ChainTraceMetadataTestApplicationFactory(true, null, version)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var tracing = application.Services.GetRequiredService<IChainTracing>();
        var rootId = tracing.BeginTrace("Action", "Controller");
        tracing.EndTrace(rootId);
        var response = Res.Ok();

        application.Services.GetRequiredService<ChainResultMetadataAttacher>()
            .Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var json = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var root = JsonSerializer.SerializeToElement(response, json).GetProperty("metadata").GetProperty("chain");
        root.GetProperty("timestamp").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
        root.TryGetProperty("service", out _).Should().BeFalse();
        root.TryGetProperty("serviceVersion", out _).Should().BeFalse();
    }

    private sealed class ChainTraceMetadataTestApplicationFactory(
        bool exposeDiagnostics,
        string? serviceName = "test-api",
        string? serviceVersion = SERVICE_VERSION) : MonicaTestApplicationFactory<ModuleChainTracing>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddJsonSerialization(options => options.DateTimeFormat = DateTimeWireFormat.SpaceSeparatedWallClock);
            builder.AddResultEnvelope(options => options.ExposeDiagnosticDetails = exposeDiagnostics);
            builder.AddChainTracing(options =>
                {
                    options.ServiceName = serviceName;
                    options.ServiceVersion = serviceVersion;
                })
                .UseControllerTracing()
                .AttachControllerTraceMetadata();
        }
    }
}
