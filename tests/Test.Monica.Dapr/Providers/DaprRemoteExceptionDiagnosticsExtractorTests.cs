using System.Text.Json;
using Dapr;
using Monica.Dapr.Providers;
using Xunit;

namespace Test.Monica.Dapr.Providers;

public sealed class DaprRemoteExceptionDiagnosticsExtractorTests
{
    private const string PREFIX = "error invoke actor method: rpc error: code = Internal desc = " +
                                  "error invoke actor method: error from actor service: (500)";

    [Theory]
    [InlineData("code")]
    [InlineData("status")]
    public void TryExtract_WhenActorReturnsDiagnosticEnvelope_ShouldSeparateAndDecodeTheResponse(string statusField)
    {
        var body = """
                   {"message":"\u8fdc\u7aef\u5f02\u5e38","STATUS_FIELD":500,"metadata":{"exception":{
                     "type":"System.InvalidOperationException","message":"value contains {braces} and \"quotes\"",
                     "stackTrace":["   at Func\u00601()","   at C:\\src\\file.cs","literal\\u0060"]}}}
                   """.Replace("STATUS_FIELD", statusField, StringComparison.Ordinal);
        var extractor = new DaprRemoteExceptionDiagnosticsExtractor();

        Assert.True(extractor.TryExtract(new DaprApiException($"{PREFIX} {body} \r\n"), out var payload));

        Assert.Equal(PREFIX, payload!.Message);
        Assert.Equal("dapr-actor", payload.Transport);
        Assert.Equal(500, payload.StatusCode);
        Assert.Equal("远端异常", payload.Response.GetProperty("message").GetString());
        var remoteException = payload.Response.GetProperty("metadata").GetProperty("exception");
        Assert.Equal("value contains {braces} and \"quotes\"", remoteException.GetProperty("message").GetString());
        var stack = remoteException.GetProperty("stackTrace");
        Assert.Equal("   at Func`1()", stack[0].GetString());
        Assert.Equal(@"   at C:\src\file.cs", stack[1].GetString());
        Assert.Equal(@"literal\u0060", stack[2].GetString());
        Assert.Equal(JsonValueKind.Object, payload.Response.ValueKind);
    }

    [Fact]
    public void TryExtract_WhenActorReturnsNewDiagnostics_ShouldRetainTheRemoteScope()
    {
        const string body = """
                            {"message":"failed","status":500,"metadata":{"traceId":"remote-trace",
                              "diagnostics":{"schemaVersion":1,"exceptionId":"e1","exceptions":[
                                {"id":"e1","type":"InvalidOperationException","message":"remote failure"}]}}}
                            """;

        Assert.True(new DaprRemoteExceptionDiagnosticsExtractor().TryExtract(
            new DaprApiException($"{PREFIX} {body}"), out var payload));

        Assert.Equal("remote-trace", payload!.Response.GetProperty("metadata").GetProperty("traceId").GetString());
        Assert.Equal("e1", payload.Response.GetProperty("metadata").GetProperty("diagnostics")
            .GetProperty("exceptionId").GetString());
    }

    [Theory]
    [InlineData("code")]
    [InlineData("status")]
    public void TryExtract_WhenActorReturnsOnlySafeError_ShouldSeparateTheRemoteFailure(string statusField)
    {
        var body = """
                   {"message":"The request could not be completed.","STATUS_FIELD":500,"metadata":{
                     "error":{"code":"internal.error","traceId":"remote-trace"},"traceId":"remote-trace"}}
                   """.Replace("STATUS_FIELD", statusField, StringComparison.Ordinal);

        Assert.True(new DaprRemoteExceptionDiagnosticsExtractor().TryExtract(
            new DaprApiException($"{PREFIX} {body}"), out var payload));

        Assert.Equal(PREFIX, payload!.Message);
        Assert.Equal(500, payload.StatusCode);
        var metadata = payload.Response.GetProperty("metadata");
        Assert.Equal("internal.error", metadata.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("remote-trace", metadata.GetProperty("error").GetProperty("traceId").GetString());
        Assert.False(metadata.TryGetProperty("exception", out _));
        Assert.False(metadata.TryGetProperty("diagnostics", out _));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{\"message\":\"truncated")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"exception\":{}}} unexpected trailer")]
    [InlineData("{\"message\":\"failed\",\"status\":500}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"businessData\":{}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":503,\"metadata\":{\"exception\":{}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"code\":500,\"metadata\":{\"exception\":{}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"STATUS\":500,\"metadata\":{\"exception\":{}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"exception\":\"raw text\"}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"error\":{\"code\":\"internal.error\"}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"error\":{\"code\":\" \",\"traceId\":\"remote-trace\"}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"error\":{\"code\":500,\"traceId\":\"remote-trace\"}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"error\":{\"code\":\"internal.error\",\"traceId\":\"\"}}}")]
    [InlineData("{\"message\":\"failed\",\"status\":500,\"metadata\":{\"error\":{\"code\":\"internal.error\",\"traceId\":\"one\",\"TraceId\":\"two\"}}}")]
    public void TryExtract_WhenPayloadIsIncompleteOrNotFailureEnvelope_ShouldDecline(string body)
    {
        Assert.False(new DaprRemoteExceptionDiagnosticsExtractor().TryExtract(
            new DaprApiException($"{PREFIX} {body}"), out var payload));
        Assert.Null(payload);
    }

    [Fact]
    public void TryExtract_WhenMessageOnlyResemblesAnActorFailure_ShouldRequireTheProviderException()
    {
        const string body = """{"message":"failed","status":500,"metadata":{"exception":{}}}""";
        var extractor = new DaprRemoteExceptionDiagnosticsExtractor();

        Assert.False(extractor.TryExtract(new InvalidOperationException($"{PREFIX} {body}"), out _));
        Assert.False(extractor.TryExtract(new DaprApiException(body), out _));
        Assert.False(extractor.TryExtract(new DaprApiException($"error from actor service: (200) {body}"), out _));
        Assert.False(extractor.TryExtract(new DaprApiException($"error from actor service: (50) {body}"), out _));
    }

    [Fact]
    public void TryExtract_WhenPayloadExceedsSizeOrDepthLimit_ShouldDeclineWithoutThrowing()
    {
        var largeMessage = new string('x', 1024 * 1024);
        var multiByteMessage = new string('远', 400_000);
        var deepData = new string('[', 70) + "0" + new string(']', 70);
        var extractor = new DaprRemoteExceptionDiagnosticsExtractor();
        foreach (var body in new[]
                 {
                     $"{{\"message\":\"{largeMessage}\",\"status\":500,\"metadata\":{{\"exception\":{{}}}}}}",
                     $"{{\"message\":\"{multiByteMessage}\",\"status\":500,\"metadata\":{{\"exception\":{{}}}}}}",
                     $"{{\"message\":\"failed\",\"status\":500,\"metadata\":{{\"exception\":{{\"data\":{deepData}}}}}}}"
                 })
        {
            Assert.False(extractor.TryExtract(new DaprApiException($"{PREFIX} {body}"), out _));
        }
    }
}
