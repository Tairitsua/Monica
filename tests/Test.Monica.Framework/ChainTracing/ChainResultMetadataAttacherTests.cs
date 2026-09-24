using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.ExceptionHandling.Exceptions;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.Core.Results;
using Monica.Framework.ChainTracing.Extensions;
using Monica.Framework.ChainTracing.Models;
using Monica.Framework.ChainTracing.Services;
using Monica.Framework.ChainTracing.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainResultMetadataAttacherTests
{
    private static readonly JsonSerializerOptions JSON = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Attach_WhenExceptionPropagatesThroughScopes_ShouldReuseTheResponseExceptionTable()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        var execution = tracing.BeginTrace("Save", "Service");
        var database = tracing.BeginTrace("INSERT INTO Flight", null, type: EChainTracingType.Database);
        var cause = new ArgumentException("UTC timestamp is invalid");
        var failure = new InvalidOperationException("Save failed", cause);
        tracing.EndTrace(database, "Error", false, cause);
        tracing.EndTrace(execution, "Exception", false, failure);
        tracing.EndTrace(root, "Exception", false, failure);
        var response = Res.Fail("Safe message");
        var document = ExceptionDiagnosticProjection.GetOrCreate(response);
        document.ExceptionId = ExceptionDiagnosticProjection.Capture(document, failure);

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var metadata = Serialize(response).GetProperty("metadata");
        var diagnostics = metadata.GetProperty("diagnostics");
        diagnostics.GetProperty("exceptions").GetArrayLength().Should().Be(2);
        var chain = metadata.GetProperty("chain");
        chain.GetProperty("exceptionId").GetString().Should().Be(document.ExceptionId);
        var executionNode = chain.GetProperty("children")[0];
        executionNode.GetProperty("exceptionId").GetString().Should().Be(document.ExceptionId);
        var innerId = diagnostics.GetProperty("exceptions").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetString() == document.ExceptionId)
            .GetProperty("innerExceptionIds")[0].GetString();
        executionNode.GetProperty("children")[0].GetProperty("exceptionId").GetString().Should().Be(innerId);
        metadata.ToString().Should().NotContain("exceptionMessage");
        metadata.TryGetProperty("exception", out _).Should().BeFalse();
    }

    [Fact]
    public void Attach_WhenIndependentExceptionsHaveTheSameMessage_ShouldKeepBothOccurrences()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        var first = tracing.BeginTrace("First", "Service");
        tracing.EndTrace(first, "Exception", false, new InvalidOperationException("same message"));
        var second = tracing.BeginTrace("Second", "Service");
        tracing.EndTrace(second, "Exception", false, new InvalidOperationException("same message"));
        tracing.EndTrace(root);
        var response = Res.Fail("Safe message");

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var metadata = Serialize(response).GetProperty("metadata");
        metadata.GetProperty("diagnostics").GetProperty("exceptions").GetArrayLength().Should().Be(2);
        var children = metadata.GetProperty("chain").GetProperty("children");
        children[0].GetProperty("exceptionId").GetString().Should()
            .NotBe(children[1].GetProperty("exceptionId").GetString());
    }

    [Fact]
    public void Attach_WhenLocalExceptionCarriesRemoteServiceContext_ShouldRetainTheLocalDocumentAndRequest()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        var failure = new InvalidOperationException("local failure while contacting actor-host");
        tracing.EndTrace(root, "Exception", false, new ContextualException(failure)
            .WithMetadata(ResultMetadataKeys.RemoteService, "actor-host"));
        var response = Res.Fail("Safe message");
        response.SetMetadata(ResultMetadataKeys.RemoteService, "actor-host");
        var document = ExceptionDiagnosticProjection.GetOrCreate(response);
        document.ExceptionId = ExceptionDiagnosticProjection.Capture(document, failure);
        document.Request = new DiagnosticRequest("POST", "/local-operation", "Action", DateTime.UtcNow);

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        ExceptionDiagnosticProjection.GetOrCreate(response).Should().BeSameAs(document);
        var metadata = Serialize(response).GetProperty("metadata");
        metadata.GetProperty("diagnostics").GetProperty("exceptionId").GetString().Should().Be(document.ExceptionId);
        metadata.GetProperty("diagnostics").GetProperty("request").GetProperty("path").GetString()
            .Should().Be("/local-operation");
        metadata.GetProperty("diagnostics").GetProperty("exceptions").GetArrayLength().Should().Be(2,
            "the chain's contextual wrapper and the handler's unwrapped cause are distinct local exceptions");
        metadata.GetProperty("chain").TryGetProperty("remote", out _).Should().BeFalse();
    }

    [Fact]
    public void Attach_WhenFailedNodeIsAlsoIsolated_ShouldReferenceTheSameExceptionWithoutRepeatingItsStack()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        var outer = tracing.BeginTrace("Outer", "Service");
        var leaked = tracing.BeginTrace("Leaked", "Service");
        tracing.EndTrace(outer);
        tracing.EndTrace(leaked, "Exception", false, new InvalidOperationException("isolated failure"));
        tracing.EndTrace(root);
        var response = Res.Fail("Safe message");

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var metadata = Serialize(response).GetProperty("metadata");
        var exceptions = metadata.GetProperty("diagnostics").GetProperty("exceptions");
        exceptions.GetArrayLength().Should().Be(1);
        var exceptionId = exceptions[0].GetProperty("id").GetString();
        metadata.GetProperty("chain_error")[0].GetProperty("exceptionId").GetString().Should().Be(exceptionId);
        metadata.GetProperty("chain_error")[0].TryGetProperty("exceptionMessage", out _).Should().BeFalse();
        metadata.GetProperty("chain").GetProperty("children")[0].GetProperty("children")[0]
            .GetProperty("exceptionId").GetString().Should().Be(exceptionId);
    }

    [Fact]
    public void Attach_WhenForwardingRemoteEnvelope_ShouldKeepEachExceptionTableInItsOwnScope()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        var remoteNode = tracing.BeginTrace("Invoke", "Proxy", type: EChainTracingType.RemoteService);
        var response = RemoteResponse("actor-host", "remote-trace");
        tracing.MergeRemoteChain(remoteNode, response);
        tracing.EndTrace(remoteNode, "Res(InternalError)", false);
        tracing.EndTrace(root, "Exception", false, new InvalidOperationException("local failure"));
        var attacher = CreateAttacher();

        attacher.Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);
        attacher.Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var metadata = Serialize(response).GetProperty("metadata");
        metadata.GetProperty("chain").GetProperty("operation").GetString().Should().Be("Action");
        metadata.EnumerateObject().Count(member => member.Name.StartsWith("chain", StringComparison.Ordinal))
            .Should().Be(1, "repeated attachment replaces the local chain instead of appending copies");
        metadata.GetProperty("diagnostics").GetProperty("exceptions")[0].GetProperty("message")
            .GetString().Should().Be("local failure");
        var remote = metadata.GetProperty("chain").GetProperty("children")[0].GetProperty("remote");
        remote.GetProperty("traceId").GetString().Should().Be("remote-trace");
        remote.GetProperty("chain").GetProperty("exceptionId").GetString().Should().Be("e1");
        remote.GetProperty("diagnostics").GetProperty("exceptions")[0].GetProperty("message")
            .GetString().Should().Be("actor-host failure");
        remote.GetProperty("chain").TryGetProperty("remote", out _).Should().BeFalse();
    }

    [Fact]
    public void Attach_WhenSeveralRemoteCallsFail_ShouldRetainEveryRemoteResponseAtItsInvocation()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        foreach (var service in new[] { "actor-host", "route-api" })
        {
            var invocation = tracing.BeginTrace("Invoke", "Proxy", type: EChainTracingType.RemoteService);
            tracing.MergeRemoteChain(invocation, RemoteResponse(service, $"{service}-trace"));
            tracing.EndTrace(invocation, "Res(InternalError)", false);
        }
        tracing.EndTrace(root, "Res(InternalError)", false);
        var response = Res.Fail("Dependencies failed");

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var children = Serialize(response).GetProperty("metadata").GetProperty("chain").GetProperty("children");
        children.GetArrayLength().Should().Be(2);
        children[0].GetProperty("remote").GetProperty("service").GetString().Should().Be("actor-host");
        children[1].GetProperty("remote").GetProperty("service").GetString().Should().Be("route-api");
        foreach (var child in children.EnumerateArray())
        {
            child.GetProperty("remote").GetProperty("diagnostics").GetProperty("exceptions")
                .GetArrayLength().Should().Be(1);
        }
    }

    [Fact]
    public void Attach_WhenForwardingTypedRemoteDiagnostics_ShouldCreateAFreshLocalDocument()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        var remoteNode = tracing.BeginTrace("Invoke", "Proxy", type: EChainTracingType.RemoteService);
        var response = Res.Fail("Safe remote message");
        response.SetMetadata(ResultMetadataKeys.RemoteService, "actor-host");
        var remoteDocument = ExceptionDiagnosticProjection.GetOrCreate(response);
        remoteDocument.ExceptionId = ExceptionDiagnosticProjection.Capture(remoteDocument,
            new InvalidOperationException("remote typed failure"));
        tracing.MergeRemoteChain(remoteNode, response);
        tracing.EndTrace(remoteNode, "Res(InternalError)", false);
        tracing.EndTrace(root, "Exception", false, new InvalidOperationException("local failure"));

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        ExceptionDiagnosticProjection.GetOrCreate(response).Should().NotBeSameAs(remoteDocument);
        var metadata = Serialize(response).GetProperty("metadata");
        metadata.GetProperty("diagnostics").GetProperty("exceptions")[0].GetProperty("message")
            .GetString().Should().Be("local failure");
        metadata.GetProperty("chain").GetProperty("children")[0].GetProperty("remote")
            .GetProperty("diagnostics").GetProperty("exceptions")[0].GetProperty("message")
            .GetString().Should().Be("remote typed failure");
    }

    [Fact]
    public void Attach_WhenForwardedEnvelopeHasNoInvocationScope_ShouldPreserveItsRemoteDiagnostics()
    {
        var tracing = CreateTracing();
        var root = tracing.BeginTrace("Action", "Controller");
        tracing.EndTrace(root);
        var response = RemoteResponse("actor-host", "remote-trace");

        CreateAttacher().Attach(tracing.GetCurrentChain(), new DefaultHttpContext(), response);

        var metadata = Serialize(response).GetProperty("metadata");
        metadata.GetProperty("chain").GetProperty("operation").GetString().Should().Be("Action");
        metadata.GetProperty("chain").GetProperty("remote").GetProperty("diagnostics")
            .GetProperty("exceptionId").GetString().Should().Be("e1");
        metadata.GetProperty("diagnostics").GetProperty("exceptions").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void EndWithException_WhenMessageContainsRemoteJson_ShouldKeepOnlyTheExceptionTypeInResult()
    {
        var tracing = CreateTracing();
        using var scope = tracing.BeginScope("Invoke", "Proxy");

        scope.EndWithException(new InvalidOperationException("remote failure {\"message\":\"remote payload\"}"));

        tracing.GetCurrentChain()!.Root!.Result.Should().Be("Exception: InvalidOperationException");
    }

    private static Res RemoteResponse(string service, string traceId)
    {
        var response = Res.Fail("Safe remote message");
        response.SetMetadata(ResultMetadataKeys.RemoteService, service);
        response.SetMetadata(ResultMetadataKeys.TraceId, traceId);
        response.SetMetadata(ResultMetadataKeys.Diagnostics, JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            exceptionId = "e1",
            exceptions = new[] { new { id = "e1", type = "System.InvalidOperationException", message = $"{service} failure", stackTrace = new[] { "at Remote.Save()" } } }
        }, JSON));
        response.SetMetadata("chain", JsonSerializer.SerializeToElement(new
        {
            operation = "Remote operation", service, exceptionId = "e1"
        }, JSON));
        return response;
    }

    private static JsonElement Serialize(Res response) => JsonSerializer.SerializeToElement(response, JSON);

    private static ChainResultMetadataAttacher CreateAttacher() => new(
        Options.Create(new ModuleResultEnvelopeOption { ExposeDiagnosticDetails = true }),
        new JsonSerializerOptionsProvider(JSON, DateTimeWireFormat.Iso8601WallClock));

    private static AsyncLocalChainTracingService CreateTracing() => new(
        Options.Create(new ModuleChainTracingOption { ServiceName = "flight-api" }),
        NullLogger<AsyncLocalChainTracingService>.Instance,
        new JsonSerializerOptionsProvider(JSON, DateTimeWireFormat.Iso8601WallClock));
}
