using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Models;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.Results;
using Monica.Core.Results.Services;
using Xunit;

namespace Test.Monica.Core.ExceptionHandling;

public sealed class ExceptionDiagnosticProjectionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Capture_WhenTheSameExceptionIsObservedTwice_ShouldReuseItsEntryAndStack()
    {
        var result = Res.Fail("Safe failure", ResStatus.InternalError);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(result);
        var exception = ThrowAndCatch(new InvalidOperationException("Database unavailable"));

        var firstId = ExceptionDiagnosticProjection.Capture(diagnostics, exception);
        var secondId = ExceptionDiagnosticProjection.Capture(diagnostics, exception);

        Assert.Same(diagnostics, ExceptionDiagnosticProjection.GetOrCreate(result));
        Assert.Equal(firstId, secondId);
        var entry = Assert.Single(diagnostics.Exceptions);
        Assert.Equal(firstId, entry.Id);
        Assert.Equal(exception.Message, entry.Message);
        Assert.Contains(entry.StackTrace, frame => frame.Contains(nameof(ThrowAndCatch), StringComparison.Ordinal));
        Assert.DoesNotContain(entry.StackTrace, frame => frame.Contains(exception.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_WhenDistinctExceptionsHaveTheSameDetails_ShouldPreserveTheirIdentityAndRecursiveFrames()
    {
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(Res.Fail("Safe failure"));
        var first = new RecursiveStackException();
        var second = new RecursiveStackException();

        var firstId = ExceptionDiagnosticProjection.Capture(diagnostics, first);
        var secondId = ExceptionDiagnosticProjection.Capture(diagnostics, second);

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, diagnostics.Exceptions.Count);
        Assert.All(diagnostics.Exceptions, entry =>
        {
            Assert.Equal("Recursive operation failed", entry.Message);
            Assert.Equal(3, entry.StackTrace.Length);
            Assert.Equal(2, entry.StackTrace.Count(frame => frame.Contains("Tree.Visit()", StringComparison.Ordinal)));
            Assert.Contains(entry.StackTrace, frame => frame.Contains("Tree.Start()", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Capture_WhenAnExceptionWrapsAnother_ShouldPreserveSeparateCausalEntries()
    {
        var inner = ThrowAndCatch(new ArgumentException("Invalid timestamp kind"));
        var outer = ThrowAndCatch(new InvalidOperationException("Saving changes failed", inner));
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(Res.Fail("Safe failure"));

        var id = ExceptionDiagnosticProjection.Capture(diagnostics, outer);

        Assert.Equal(2, diagnostics.Exceptions.Count);
        var outerEntry = Assert.Single(diagnostics.Exceptions, entry => entry.Id == id);
        var innerId = Assert.Single(outerEntry.InnerExceptionIds!);
        var innerEntry = Assert.Single(diagnostics.Exceptions, entry => entry.Id == innerId);
        Assert.Equal(outer.GetType().FullName, outerEntry.Type);
        Assert.Equal(outer.Message, outerEntry.Message);
        Assert.Equal(inner.GetType().FullName, innerEntry.Type);
        Assert.Equal(inner.Message, innerEntry.Message);
        Assert.DoesNotContain(inner.Message, outerEntry.Message, StringComparison.Ordinal);
        Assert.All(diagnostics.Exceptions.SelectMany(entry => entry.StackTrace), frame =>
        {
            Assert.DoesNotContain("--->", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("End of inner exception", frame, StringComparison.Ordinal);
        });
        Assert.Equal(innerId, ExceptionDiagnosticProjection.Capture(diagnostics, inner));
        Assert.Equal(2, diagnostics.Exceptions.Count);
    }

    [Fact]
    public void Capture_WhenAggregateBranchesShareAnException_ShouldPreserveAllBranchesWithoutDuplicatingIt()
    {
        var shared = new ArgumentException("Shared cause");
        var firstBranch = new InvalidOperationException("First operation failed", shared);
        var secondBranch = new IOException("Second operation failed", shared);
        var aggregate = new AggregateException("Parallel operations failed", firstBranch, secondBranch);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(Res.Fail("Safe failure"));

        var id = ExceptionDiagnosticProjection.Capture(diagnostics, aggregate);

        Assert.Equal(4, diagnostics.Exceptions.Count);
        var root = Assert.Single(diagnostics.Exceptions, entry => entry.Id == id);
        Assert.Equal(2, root.InnerExceptionIds!.Length);
        var branches = diagnostics.Exceptions.Where(entry => root.InnerExceptionIds.Contains(entry.Id)).ToArray();
        Assert.Contains(branches, entry => entry.Message == firstBranch.Message);
        Assert.Contains(branches, entry => entry.Message == secondBranch.Message);
        var sharedEntry = Assert.Single(diagnostics.Exceptions, entry => entry.Message == shared.Message);
        Assert.All(branches, branch => Assert.Equal(sharedEntry.Id, Assert.Single(branch.InnerExceptionIds!)));
    }

    [Fact]
    public void Capture_WhenTheTransportExtractsAResponse_ShouldKeepRemoteJsonOutsideTheLocalMessage()
    {
        var remoteResponse = CreateLegacyResponse();
        var exception = new ExtractedTransportException(
            "Actor invocation failed: " + remoteResponse.GetRawText(), remoteResponse);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(Res.Fail("Safe failure"));

        var id = ExceptionDiagnosticProjection.Capture(diagnostics, exception, [new FixtureExtractor()]);

        var entry = Assert.Single(diagnostics.Exceptions);
        Assert.Equal(id, entry.Id);
        Assert.Equal("Actor invocation failed", entry.Message);
        Assert.DoesNotContain("metadata", entry.Message, StringComparison.Ordinal);
        var remote = Assert.IsType<RemoteDiagnosticResponse>(entry.Remote);
        Assert.Equal("fixture-actor", remote.Transport);
        Assert.Equal(500, JsonSerializer.SerializeToElement(remote, Json).GetProperty("status").GetInt32());
        Assert.Equal("flight-actor-host", remote.Service);
        Assert.Equal("remote-trace-123", remote.TraceId);
        Assert.Equal("Remote request failed", remote.Message);
        Assert.Equal("PUT", remote.Method);
        Assert.Equal("/actors/FlightActor/flight-123/method/Update", remote.Path);
        Assert.NotNull(remote.Diagnostics);
        Assert.NotEmpty(remote.Diagnostics.Exceptions);
        Assert.All(remote.Diagnostics.Exceptions, remoteEntry =>
            Assert.DoesNotContain(diagnostics.Exceptions, localEntry => ReferenceEquals(localEntry, remoteEntry)));
    }

    [Fact]
    public void CaptureRemote_WhenLegacyChainsRepeatAStack_ShouldReplaceThemWithOneExceptionReference()
    {
        var remote = ExceptionDiagnosticProjection.CaptureRemote(CreateLegacyResponse(), "fixture-actor", 500);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        var entry = Assert.Single(remote.Diagnostics.Exceptions);
        Assert.Contains(entry.StackTrace, frame => frame.Contains("Database.Save()", StringComparison.Ordinal));
        Assert.NotNull(remote.Chain);
        var chain = Assert.IsType<JsonObject>(remote.Chain);
        Assert.False(chain.ContainsKey("exceptionMessage"));
        Assert.Equal(entry.Id, chain["exceptionId"]!.GetValue<string>());
        var child = Assert.IsType<JsonObject>(chain["children"]![0]);
        Assert.False(child.ContainsKey("exceptionMessage"));
        Assert.Equal(entry.Id, child["exceptionId"]!.GetValue<string>());
        var serialized = JsonSerializer.Serialize(remote, Json);
        Assert.Equal(1, CountOccurrences(serialized, "Database.Save()"));
    }

    [Fact]
    public void CaptureRemote_WhenALegacyChainEmbedsAnotherResponse_ShouldExtractItAndCleanTheResultSummary()
    {
        var nestedResponse = CreateLegacyResponse();
        var message = "error invoke actor method: error from actor service: (500) " + nestedResponse.GetRawText();
        string[] stack = ["Dapr.DaprApiException: " + message, "   at ActorClient.Invoke()"];
        var response = JsonSerializer.SerializeToElement(new
        {
            code = 500,
            message = "Forwarding service failed",
            metadata = new
            {
                exception = new { type = "Dapr.DaprApiException", message, stackTrace = stack },
                chain = new { operation = "InvokeActor", result = message, exceptionMessage = stack }
            }
        });

        var remote = ExceptionDiagnosticProjection.CaptureRemote(response);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        var entry = Assert.Single(remote.Diagnostics.Exceptions);
        Assert.DoesNotContain("{", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Remote);
        Assert.Equal("flight-actor-host", entry.Remote.Service);
        Assert.Equal("remote-trace-123", entry.Remote.TraceId);
        Assert.NotNull(entry.Remote.Diagnostics);
        Assert.Single(entry.Remote.Diagnostics.Exceptions);
        Assert.NotNull(remote.Chain);
        var summary = remote.Chain["result"]!.GetValue<string>();
        Assert.DoesNotContain("{", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureRemote_WhenLegacyCallsShareAStackButHaveDifferentResponses_ShouldPreserveBothRemoteFailures()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            code = 500,
            message = "Two remote calls failed",
            metadata = new
            {
                chain = new
                {
                    operation = "Parallel actor calls",
                    children = new[]
                    {
                        CreateCall("first-trace", "First remote failure"),
                        CreateCall("second-trace", "Second remote failure")
                    }
                }
            }
        });

        var remote = ExceptionDiagnosticProjection.CaptureRemote(payload);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        Assert.Equal(2, remote.Diagnostics.Exceptions.Count);
        Assert.All(remote.Diagnostics.Exceptions, entry =>
        {
            Assert.Equal("Dapr.DaprApiException", entry.Type);
            Assert.DoesNotContain("{", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("metadata", entry.Message, StringComparison.Ordinal);
            Assert.NotNull(entry.Remote);
        });
        var first = Assert.Single(remote.Diagnostics.Exceptions, entry => entry.Remote?.TraceId == "first-trace");
        var second = Assert.Single(remote.Diagnostics.Exceptions, entry => entry.Remote?.TraceId == "second-trace");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(first.StackTrace, second.StackTrace);
        Assert.Equal("First remote failure", first.Remote!.Message);
        Assert.Equal("Second remote failure", second.Remote!.Message);
        Assert.NotNull(remote.Chain);
        Assert.Equal(first.Id, remote.Chain["children"]![0]!["exceptionId"]!.GetValue<string>());
        Assert.Equal(second.Id, remote.Chain["children"]![1]!["exceptionId"]!.GetValue<string>());

        static object CreateCall(string traceId, string message)
        {
            var response = JsonSerializer.Serialize(new
            {
                code = 500,
                message,
                metadata = new { error = new { code = "remote.failed", traceId, service = "flight-actor-host" } }
            });
            var failure = "error invoke actor method: error from actor service: (500) " + response;
            return new
            {
                operation = "InvokeActor",
                result = failure,
                exceptionMessage = new[] { "Dapr.DaprApiException: " + failure, "   at ActorClient.Invoke()" }
            };
        }
    }

    [Fact]
    public void CaptureRemote_WhenResponseAlreadyUsesDiagnostics_ShouldRetainItsGraphAndCorrelation()
    {
        var result = Res.Fail("Remote operation failed", ResStatus.InternalError)
            .SetError(new ResultError("remote.failed", "remote-trace", "remote-service"));
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(result);
        var rootId = ExceptionDiagnosticProjection.Capture(diagnostics,
            new InvalidOperationException("Wrapper", new ArgumentException("Cause")));
        diagnostics.ExceptionId = rootId;

        var remote = ExceptionDiagnosticProjection.CaptureRemote(result, Json, "fixture-rpc", 500);

        Assert.NotNull(remote);
        Assert.Equal("remote-service", remote.Service);
        Assert.Equal("remote-trace", remote.TraceId);
        Assert.NotNull(remote.Diagnostics);
        Assert.Equal(rootId, remote.Diagnostics.ExceptionId);
        Assert.Equal(2, remote.Diagnostics.Exceptions.Count);
        var root = Assert.Single(remote.Diagnostics.Exceptions, entry => entry.Id == rootId);
        var causeId = Assert.Single(root.InnerExceptionIds!);
        Assert.Contains(remote.Diagnostics.Exceptions, entry => entry.Id == causeId && entry.Message == "Cause");
    }

    [Fact]
    public void CaptureRemote_WhenCurrentEntriesHaveDifferentIdsAndIdenticalDetails_ShouldPreserveTheirIdentities()
    {
        using var payload = JsonDocument.Parse("""
            {
              "code": 500,
              "message": "Parallel failure",
              "metadata": {
                "diagnostics": {
                  "schemaVersion": 1,
                  "exceptionId": "root",
                  "exceptions": [
                    { "id": "root", "type": "System.AggregateException", "message": "Parallel failure",
                      "stackTrace": [], "innerExceptionIds": ["first", "second"] },
                    { "id": "first", "type": "System.InvalidOperationException", "message": "Same details",
                      "stackTrace": ["at Worker.Run()"] },
                    { "id": "second", "type": "System.InvalidOperationException", "message": "Same details",
                      "stackTrace": ["at Worker.Run()"] }
                  ]
                },
                "chain": {
                  "operation": "Parallel", "exceptionId": "root",
                  "children": [
                    { "operation": "First", "exceptionId": "first" },
                    { "operation": "Second", "exceptionId": "second" }
                  ]
                }
              }
            }
            """);

        var remote = ExceptionDiagnosticProjection.CaptureRemote(payload.RootElement);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        Assert.Equal(3, remote.Diagnostics.Exceptions.Count);
        var root = Assert.Single(remote.Diagnostics.Exceptions, entry => entry.Id == remote.Diagnostics.ExceptionId);
        Assert.Equal(2, root.InnerExceptionIds!.Length);
        Assert.NotEqual(root.InnerExceptionIds[0], root.InnerExceptionIds[1]);
        Assert.All(root.InnerExceptionIds, id =>
        {
            var entry = Assert.Single(remote.Diagnostics.Exceptions, candidate => candidate.Id == id);
            Assert.Equal("Same details", entry.Message);
        });
        Assert.NotNull(remote.Chain);
        var firstReference = remote.Chain["children"]![0]!["exceptionId"]!.GetValue<string>();
        var secondReference = remote.Chain["children"]![1]!["exceptionId"]!.GetValue<string>();
        Assert.NotEqual(firstReference, secondReference);
        Assert.Contains(firstReference, root.InnerExceptionIds);
        Assert.Contains(secondReference, root.InnerExceptionIds);
    }

    [Fact]
    public void CaptureRemote_WhenCurrentDiagnosticsContainNestedRemote_ShouldRetainItsBoundaryAndAdditionalChains()
    {
        var result = Res.Fail("Forwarding failed", ResStatus.InternalError);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(result);
        diagnostics.ExceptionId = ExceptionDiagnosticProjection.Capture(diagnostics,
            new InvalidOperationException("Invocation failed"));
        var nestedDiagnostics = ExceptionDiagnosticProjection.GetOrCreate(Res.Fail("Remote failed"));
        nestedDiagnostics.ExceptionId = ExceptionDiagnosticProjection.Capture(nestedDiagnostics,
            new IOException("Remote storage unavailable"));
        var nestedId = nestedDiagnostics.ExceptionId;
        Assert.Single(diagnostics.Exceptions).Remote = new RemoteDiagnosticResponse
        {
            Transport = "fixture-rpc",
            Status = 503,
            Message = "Remote service unavailable",
            Service = "storage-service",
            TraceId = "storage-trace",
            Method = "POST",
            Path = "/storage/commit",
            Diagnostics = nestedDiagnostics,
            Chain = new JsonObject { ["operation"] = "Commit", ["exceptionId"] = nestedId },
            AdditionalChains =
            [
                new JsonObject { ["operation"] = "DetachedWrite", ["exceptionId"] = nestedId },
                new JsonObject { ["operation"] = "Rollback", ["result"] = "Success" }
            ],
            Truncated = true
        };

        var remote = ExceptionDiagnosticProjection.CaptureRemote(result, Json);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        var nested = Assert.Single(remote.Diagnostics.Exceptions).Remote;
        Assert.NotNull(nested);
        Assert.Equal("fixture-rpc", nested.Transport);
        Assert.Equal(503, nested.Status);
        Assert.Equal("Remote service unavailable", nested.Message);
        Assert.Equal("storage-service", nested.Service);
        Assert.Equal("storage-trace", nested.TraceId);
        Assert.Equal("POST", nested.Method);
        Assert.Equal("/storage/commit", nested.Path);
        Assert.True(nested.Truncated);
        Assert.NotNull(nested.Diagnostics);
        var nestedEntry = Assert.Single(nested.Diagnostics.Exceptions);
        Assert.Equal("Remote storage unavailable", nestedEntry.Message);
        Assert.Equal(nestedEntry.Id, nested.Diagnostics.ExceptionId);
        Assert.NotNull(nested.Chain);
        Assert.Equal(nestedEntry.Id, nested.Chain["exceptionId"]!.GetValue<string>());
        Assert.NotNull(nested.AdditionalChains);
        Assert.Equal(2, nested.AdditionalChains.Count);
        Assert.Equal("DetachedWrite", nested.AdditionalChains[0]!["operation"]!.GetValue<string>());
        Assert.Equal(nestedEntry.Id, nested.AdditionalChains[0]!["exceptionId"]!.GetValue<string>());
        Assert.Equal("Rollback", nested.AdditionalChains[1]!["operation"]!.GetValue<string>());
        Assert.Equal("Success", nested.AdditionalChains[1]!["result"]!.GetValue<string>());
    }

    [Fact]
    public void CaptureRemote_WhenNestedRemoteHasOnlyASummary_ShouldNotInventAnException()
    {
        var result = Res.Fail("Invocation failed", ResStatus.BadGateway);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(result);
        diagnostics.ExceptionId = ExceptionDiagnosticProjection.Capture(diagnostics,
            new InvalidOperationException("Remote request rejected"));
        Assert.Single(diagnostics.Exceptions).Remote = new RemoteDiagnosticResponse
        {
            Transport = "fixture-rpc",
            Status = 429,
            Message = "Too many requests",
            Service = "limited-service",
            TraceId = "limited-trace",
            Method = "GET",
            Path = "/limited-operation"
        };

        var remote = ExceptionDiagnosticProjection.CaptureRemote(result, Json);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        var nested = Assert.Single(remote.Diagnostics.Exceptions).Remote;
        Assert.NotNull(nested);
        Assert.Equal(429, nested.Status);
        Assert.Equal("Too many requests", nested.Message);
        Assert.Equal("limited-service", nested.Service);
        Assert.Equal("limited-trace", nested.TraceId);
        Assert.Equal("GET", nested.Method);
        Assert.Equal("/limited-operation", nested.Path);
        Assert.Null(nested.Diagnostics);
        Assert.Null(nested.Chain);
        Assert.Null(nested.AdditionalChains);
        Assert.False(nested.Truncated);
    }

    [Fact]
    public void CaptureRemote_WhenJsonDecodesStackSymbols_ShouldPreserveLiteralBackslashesInMessages()
    {
        using var document = JsonDocument.Parse("""
            {
              "code": 500,
              "message": "Cannot open C:\\updates\\u00601.txt",
              "metadata": {
                "exception": {
                  "type": "System.IO.IOException",
                  "message": "Cannot open C:\\updates\\u00601.txt",
                  "stackTrace": ["   at Workflow.Invoke(Func\u00601 callback)"]
                }
              }
            }
            """);

        var remote = ExceptionDiagnosticProjection.CaptureRemote(document.RootElement);

        Assert.NotNull(remote);
        Assert.Equal(@"Cannot open C:\updates\u00601.txt", remote.Message);
        Assert.NotNull(remote.Diagnostics);
        var entry = Assert.Single(remote.Diagnostics.Exceptions);
        Assert.Equal(@"Cannot open C:\updates\u00601.txt", entry.Message);
        Assert.Contains(entry.StackTrace, frame => frame.Contains("Func`1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"metadata\":false}")]
    [InlineData("{\"code\":{},\"message\":[],\"metadata\":{\"exception\":42,\"chain\":\"invalid\",\"diagnostics\":false}}")]
    [InlineData("{\"code\":500,\"metadata\":{\"diagnostics\":{\"exceptions\":[null,42,{\"stackTrace\":false}]}}}")]
    public void CaptureRemote_WhenRemoteFieldsAreMalformed_ShouldNotThrow(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Record.Exception(() => ExceptionDiagnosticProjection.CaptureRemote(document.RootElement));

        Assert.Null(exception);
    }

    [Fact]
    public void CaptureRemote_WhenThePayloadIsOversized_ShouldBoundTheProjectionAndMarkTruncation()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            code = 500,
            message = new string('x', 2_000_000)
        });

        var remote = ExceptionDiagnosticProjection.CaptureRemote(payload, "fixture-rpc", 500);

        Assert.NotNull(remote);
        var projected = JsonSerializer.Serialize(remote, Json);
        Assert.True(projected.Length < payload.GetRawText().Length);
        Assert.Contains("\"truncated\":true", projected, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureRemote_WhenOnlyTheSummaryIsTruncated_ShouldMarkTheRemoteResponse()
    {
        var message = new string('x', 6_000);
        var payload = JsonSerializer.SerializeToElement(new
        {
            code = 500,
            message,
            metadata = new { error = new { code = "remote.failed", traceId = "remote-trace" } }
        });

        var remote = ExceptionDiagnosticProjection.CaptureRemote(payload);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Message);
        Assert.True(remote.Message.Length < message.Length);
        Assert.True(remote.Truncated);
        Assert.Equal(500, remote.Status);
        Assert.Equal("remote-trace", remote.TraceId);
        Assert.Null(remote.Diagnostics);
    }

    [Fact]
    public void CaptureRemote_WhenAnEnvelopeExceedsThePayloadLimit_ShouldRetainItsKnownStatus()
    {
        var result = Res.Fail("Upstream unavailable", ResStatus.ServiceUnavailable)
            .SetMetadata("chain", new { operation = new string('x', 2_000_000) });

        var remote = ExceptionDiagnosticProjection.CaptureRemote(result, Json, "fixture-rpc");

        Assert.NotNull(remote);
        Assert.Equal(503, remote.Status);
        Assert.Equal("fixture-rpc", remote.Transport);
        Assert.True(remote.Truncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureRemote_WhenLegacyAggregateHasMultipleBranches_ShouldPreserveCausesAndOwnStacks(bool withStacks)
    {
        Exception[] branches =
        [
            new InvalidOperationException("First operation failed"),
            new IOException("Second operation failed"),
            new ApplicationException("Third operation failed")
        ];
        if (withStacks)
        {
            branches[0] = ThrowAndCatch(branches[0]);
            branches[1] = ThrowAndCatch(branches[1]);
        }
        Exception aggregate = new AggregateException("Parallel operations failed", branches);
        if (withStacks) aggregate = ThrowAndCatch(aggregate);
        var legacyStack = aggregate.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var payload = JsonSerializer.SerializeToElement(new
        {
            code = 500,
            message = "Remote execution failed",
            metadata = new
            {
                exception = new { type = aggregate.GetType().FullName, message = aggregate.Message, stackTrace = legacyStack }
            }
        });

        var remote = ExceptionDiagnosticProjection.CaptureRemote(payload);

        Assert.NotNull(remote);
        Assert.NotNull(remote.Diagnostics);
        Assert.Equal(4, remote.Diagnostics.Exceptions.Count);
        var root = Assert.Single(remote.Diagnostics.Exceptions, entry => entry.Id == remote.Diagnostics.ExceptionId);
        Assert.Equal(aggregate.Message, root.Message);
        Assert.Equal(OwnFrames(aggregate), root.StackTrace);
        Assert.Equal(3, root.InnerExceptionIds!.Length);
        foreach (var branch in branches)
        {
            var entry = Assert.Single(remote.Diagnostics.Exceptions, candidate => candidate.Message == branch.Message);
            Assert.Contains(entry.Id, root.InnerExceptionIds);
            Assert.Equal(branch.GetType().FullName, entry.Type);
            Assert.Equal(OwnFrames(branch), entry.StackTrace);
        }
        Assert.All(remote.Diagnostics.Exceptions, entry =>
        {
            Assert.DoesNotContain("<---", entry.Message, StringComparison.Ordinal);
            Assert.All(entry.StackTrace, frame => Assert.DoesNotContain("<---", frame, StringComparison.Ordinal));
        });

        static string[] OwnFrames(Exception exception) => exception.StackTrace?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    }

    [Fact]
    public void Capture_WhenTheCausalGraphIsExcessivelyDeep_ShouldBoundItAndMarkTruncation()
    {
        Exception exception = new InvalidOperationException("Original cause");
        for (var index = 0; index < 512; index++)
            exception = new InvalidOperationException($"Wrapper {index}", exception);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(Res.Fail("Safe failure"));

        ExceptionDiagnosticProjection.Capture(diagnostics, exception);

        Assert.NotEmpty(diagnostics.Exceptions);
        Assert.True(diagnostics.Exceptions.Count < 513);
        Assert.Contains("\"truncated\":true", JsonSerializer.Serialize(diagnostics, Json), StringComparison.Ordinal);
        var capturedIds = diagnostics.Exceptions.Select(entry => entry.Id).ToHashSet();
        Assert.All(diagnostics.Exceptions.SelectMany(entry => entry.InnerExceptionIds ?? []),
            id => Assert.Contains(id, capturedIds));
    }

    [Fact]
    public void PrepareForPresentation_WhenDiagnosticsArePrivate_ShouldRemoveModernAndLegacyMembers()
    {
        var result = Res.Fail("Safe failure", ResStatus.InternalError)
            .SetMetadata("exception", "secret-exception")
            .SetMetadata("chain", "secret-chain")
            .SetMetadata("chain_1", "secret-remote-chain")
            .SetMetadata("Chain_2", "secret-additional-chain")
            .SetMetadata("availableActions", new[] { "retry" })
            .SetMetadata("chainOfCustody", "public-domain-value");
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(result);
        ExceptionDiagnosticProjection.Capture(diagnostics, new InvalidOperationException("secret-diagnostics"));

        result.PrepareForPresentation(Json, new DefaultResultErrorMessageProvider(), "public-trace");

        var serialized = JsonSerializer.Serialize(result, Json);
        Assert.DoesNotContain("secret-", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"diagnostics\"", serialized, StringComparison.Ordinal);
        Assert.Contains("availableActions", serialized, StringComparison.Ordinal);
        Assert.Contains("chainOfCustody", serialized, StringComparison.Ordinal);
        Assert.True(result.TryGetError(Json, out var error));
        Assert.Equal("public-trace", error.TraceId);
        Assert.Equal(ResStatus.InternalError, result.Status);
        Assert.Equal("Safe failure", result.Message);
    }

    [Fact]
    public void PrepareForPresentation_WhenDetailsAreEnabled_ShouldRetainTheDiagnosticGraph()
    {
        var result = Res.Fail("Safe failure", ResStatus.InternalError);
        var diagnostics = ExceptionDiagnosticProjection.GetOrCreate(result);
        var id = ExceptionDiagnosticProjection.Capture(diagnostics, new InvalidOperationException("Diagnostic detail"));

        result.PrepareForPresentation(Json, new DefaultResultErrorMessageProvider(), "trace",
            exposeReservedDiagnostics: true);

        Assert.Same(diagnostics, ExceptionDiagnosticProjection.GetOrCreate(result));
        Assert.Equal(id, Assert.Single(diagnostics.Exceptions).Id);
        Assert.Contains("Diagnostic detail", JsonSerializer.Serialize(result, Json), StringComparison.Ordinal);
    }

    private static JsonElement CreateLegacyResponse()
    {
        string[] legacyStack =
        [
            "System.InvalidOperationException: Remote storage failed",
            "   at Database.Save()",
            "   at FlightActor.Update()"
        ];
        return JsonSerializer.SerializeToElement(new
        {
            code = 500,
            message = "Remote request failed",
            metadata = new
            {
                error = new { code = "internal.unexpected", traceId = "remote-trace-123", service = "flight-actor-host" },
                exception = new
                {
                    type = "System.InvalidOperationException",
                    message = "Remote storage failed",
                    stackTrace = legacyStack,
                    method = "PUT",
                    path = "/actors/FlightActor/flight-123/method/Update"
                },
                chain = new
                {
                    type = "Rpc",
                    operation = "FlightActor.Update",
                    result = "Error[5ms]",
                    exceptionMessage = legacyStack,
                    children = new[]
                    {
                        new { type = "Database", operation = "INSERT", result = "Error[1ms]", exceptionMessage = legacyStack }
                    }
                }
            }
        });
    }

    private static Exception ThrowAndCatch(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    private static int CountOccurrences(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private sealed class ExtractedTransportException(string message, JsonElement response) : Exception(message)
    {
        public JsonElement Response { get; } = response;

        public override string ToString() => throw new InvalidOperationException("Diagnostic projection must not call ToString.");
    }

    private sealed class RecursiveStackException() : Exception("Recursive operation failed")
    {
        public override string StackTrace => "   at Tree.Visit()\n   at Tree.Visit()\n   at Tree.Start()";
    }

    private sealed class FixtureExtractor : IRemoteExceptionDiagnosticsExtractor
    {
        public bool TryExtract(Exception exception, [NotNullWhen(true)] out RemoteExceptionPayload? payload)
        {
            if (exception is ExtractedTransportException transportException)
            {
                payload = new RemoteExceptionPayload("Actor invocation failed", transportException.Response, "fixture-actor", 500);
                return true;
            }

            payload = null;
            return false;
        }
    }
}
