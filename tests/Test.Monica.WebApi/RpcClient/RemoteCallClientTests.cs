using System.Diagnostics;
using System.Dynamic;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.DependencyInjection.Abstractions;
using Monica.Rpc.Abstractions;
using Monica.Rpc.Annotations;
using Monica.Rpc.Models;
using Xunit;

namespace Test.Monica.Rpc;

public sealed class RemoteCallClientTests
{
    private static readonly RemoteCallContext Call = new(
        new RpcServiceDescriptor("Orders", "private-app"), "CreateOrder", "orders/{id}");

    [Theory]
    [InlineData(200, 200)]
    [InlineData(201, 201)]
    [InlineData(400, 400)]
    [InlineData(401, 401)]
    [InlineData(409, 409)]
    [InlineData(500, 500)]
    public async Task Invoke_WhenEnvelopeAgreesWithHttp_ShouldPreserveApplicationResult(int http, int status)
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var json = $$"""{"code":{{status}},"message":"Application message","data":null}""";
        using var client = Client((_, _) => Task.FromResult(Response(http, json)));
        var result = await Invoke<Res<string?>>(host, client);
        Assert.Equal((ResStatus)status, result.Status);
        Assert.Equal("Application message", result.Message);
        Assert.Null(result.Data);
        Assert.Equal(status is 200 or 201, result.IsOk());
    }

    [Theory]
    [InlineData(200, JsonIgnoreCondition.Never)]
    [InlineData(200, JsonIgnoreCondition.WhenWritingNull)]
    [InlineData(200, JsonIgnoreCondition.WhenWritingDefault)]
    [InlineData(201, JsonIgnoreCondition.WhenWritingNull)]
    [InlineData(201, JsonIgnoreCondition.WhenWritingDefault)]
    public async Task Invoke_WhenSuccessfulMessageIsNull_ShouldAcceptHostSerializedEnvelope(
        int status, JsonIgnoreCondition ignoreCondition)
    {
        await using var host = await new RpcFactory(ignoreCondition: ignoreCondition)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var options = host.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var downstream = Res.Create("payload", (ResStatus)status);
        Assert.Null(downstream.Message);
        var json = JsonSerializer.Serialize(downstream, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(ignoreCondition == JsonIgnoreCondition.Never,
            document.RootElement.TryGetProperty("message", out _));
        using var client = Client((_, _) => Task.FromResult(Response(status, json)));

        var result = await Invoke<Res<string>>(host, client);

        Assert.Equal((ResStatus)status, result.Status);
        Assert.Equal("payload", result.Data);
        Assert.True(result.IsOk());
        Assert.False(result.TryGetError(options, out _));
    }

    [Theory]
    [InlineData(200, """{"code":200,"message":"","message":null,"data":"payload"}""")]
    [InlineData(200, """{"code":200,"code":200,"data":"payload"}""")]
    [InlineData(200, """{"code":"200","data":"payload"}""")]
    [InlineData(200, """{"code":0,"data":"payload"}""")]
    [InlineData(200, """{"code":999,"data":"payload"}""")]
    [InlineData(200, """{"code":201,"data":"payload"}""")]
    [InlineData(201, """{"code":200,"data":"payload"}""")]
    [InlineData(200, """{"code":500,"data":"payload"}""")]
    [InlineData(200, """{"code":200,"data":{"wrong":"type"}}""")]
    [InlineData(200, """{"code":200,"metadata":{"error":{"stackTrace":"secret"}}}""")]
    public async Task Invoke_WhenSuccessfulMessageIsOptional_ShouldStillRejectInvalidEnvelopes(int http, string json)
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) => Task.FromResult(Response(http, json)));

        var result = await Invoke<Res<string>>(host, client);

        Assert.Equal(ResStatus.BadGateway, result.Status);
        Assert.Equal(ResultErrorCodes.DependencyInvalidResponse, Error(host, result).Code);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(400, "dependency.rejected")]
    [InlineData(500, "dependency.failed")]
    public async Task Invoke_WhenFailedResponseOmitsMessage_ShouldNotAcceptItAsApplicationEnvelope(int http, string code)
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) => Task.FromResult(Response(http, $$"""{"code":{{http}}}""")));

        var result = await Invoke<Res>(host, client);

        Assert.Equal(ResStatus.BadGateway, result.Status);
        Assert.Equal(code, Error(host, result).Code);
    }

    [Theory]
    [InlineData(200, """{"status":200,"message":"alias mismatch"}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":500,"message":"mismatch"}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":200,"code":200,"message":""}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":"200","message":""}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":999,"message":""}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":0,"message":""}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":200,"message":[] }""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":200,"message":"","data":{"wrong":"type"}}""", 502, "dependency.invalid_response")]
    [InlineData(200, """{"code":200,"message":"","metadata":{"error":{"stackTrace":"secret"}}}""", 502, "dependency.invalid_response")]
    [InlineData(400, """{"status":400,"title":"ProblemDetails"}""", 502, "dependency.rejected")]
    [InlineData(429, "<html>secret</html>", 503, "dependency.rate_limited")]
    [InlineData(503, "<html>secret</html>", 503, "dependency.unavailable")]
    [InlineData(504, "<html>secret</html>", 504, "dependency.timeout")]
    [InlineData(500, "<html>secret</html>", 502, "dependency.failed")]
    public async Task Invoke_WhenWireContractFails_ShouldClassifyWithoutExposingContent(
        int http, string json, int status, string code)
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) => Task.FromResult(Response(http, json)));
        var result = await Invoke<Res<string>>(host, client);
        Assert.Equal((ResStatus)status, result.Status);
        Assert.Equal(code, Error(host, result).Code);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Invoke_WhenRateLimitedInWorker_ShouldRecordRetryAfterAndSameNonemptyTrace()
    {
        var logs = new RecordingLogs();
        await using var host = await new RpcFactory(logs: logs).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) =>
        {
            var response = Response(429, "{}");
            response.Headers.TryAddWithoutValidation("Retry-After", "17");
            return Task.FromResult(response);
        });
        Activity.Current = null;
        var result = await Invoke<Res>(host, client);
        var error = Error(host, result);
        Assert.Equal(32, error.TraceId.Length);
        var entry = Assert.Single(logs.Entries);
        Assert.Contains(error.TraceId, entry);
        Assert.Contains("RetryAfter 17", entry);
        Assert.Contains("dependency.rate_limited", entry);
        Assert.DoesNotContain("private-app", entry);
        Assert.DoesNotContain("secret-query", entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_WhenDownstreamCarriesExceptionDetails_ShouldFollowTheHostDiagnosticSwitch(bool expose)
    {
        await using var host = await new RpcFactory(exposeDiagnostics: expose)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var jsonOptions = host.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var downstream = Res.Fail("Safe downstream failure", ResStatus.InternalError)
            .SetError(new ResultError("domain.failed", "origin-trace"))
            .SetMetadata("exception", new { stackTrace = "downstream-secret" });
        using var client = Client((_, _) => Task.FromResult(Response(500, JsonSerializer.Serialize(downstream, jsonOptions))));
        var result = await Invoke<Res>(host, client);
        var output = JsonSerializer.Serialize(result);

        Assert.Equal(ResStatus.InternalError, result.Status);
        if (expose) Assert.Contains("downstream-secret", output);
        else Assert.DoesNotContain("downstream-secret", output);
        // A forwarded error that does not identify its origin gains the immediate dependency's identity.
        var error = Error(host, result);
        Assert.Equal("Orders", error.Service);
        Assert.Equal("CreateOrder", error.Operation);
        Assert.Equal("origin-trace", error.TraceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_WhenDecoded_ShouldStampTargetServiceForChainCorrelation(bool expose)
    {
        await using var host = await new RpcFactory(exposeDiagnostics: expose)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) => Task.FromResult(Response(200, """{"code":200,"message":"","data":"ok"}""")));
        var result = await Invoke<Res>(host, client);

        var output = JsonSerializer.Serialize(result);
        if (expose) Assert.Contains("\"remoteService\":\"Orders\"", output);
        else Assert.DoesNotContain("remoteService", output);
    }

    [Fact]
    public async Task Invoke_WhenTransportFails_ShouldStampAttemptedTargetService()
    {
        await using var host = await new RpcFactory()
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("unreachable")));
        var result = await Invoke<Res>(host, client);

        Assert.Equal(ResStatus.ServiceUnavailable, result.Status);
        // The local envelope bypasses remote presentation, so the stamp stays for in-process chain consumers.
        Assert.Contains("\"remoteService\":\"Orders\"", JsonSerializer.Serialize(result));
        Assert.Equal("Orders", Error(host, result).Service);
    }

    [Fact]
    public async Task Invoke_WhenReturningTypedErrorAcrossHop_ShouldPreserveOriginAndRemoveDiagnosticMetadata()
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var jsonOptions = host.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var downstream = Res.Fail("Safe downstream failure", ResStatus.InternalError)
            .SetError(new ResultError("domain.failed", "origin-trace", "Inventory", "Reserve"))
            .SetMetadata("request", "secret").SetMetadata("chain", "secret")
            .SetMetadata("actions", new[] { "inspect" });
        using var client = Client((_, _) => Task.FromResult(Response(500, JsonSerializer.Serialize(downstream, jsonOptions))));
        var result = await Invoke<Res>(host, client);
        Assert.Equal("origin-trace", Error(host, result).TraceId);
        Assert.Equal(ResStatus.InternalError, result.Status);
        var output = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("secret", output);
        Assert.Contains("actions", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_WhenDecodedBodyExceedsLimit_ShouldRejectEvenCompressedContent(bool compressed)
    {
        await using var host = await new RpcFactory(maxBytes: 64).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { code = 200, message = "", data = new string('a', 1000) });
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(bytes);
            bytes = output.ToArray();
        }
        using var client = Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("application/json");
            if (compressed) response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        var result = await Invoke<Res<string>>(host, client);
        Assert.Equal("dependency.invalid_response", Error(host, result).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invoke_WhenBodyStalls_ShouldOwnDeadlineOrPropagateCallerCancellationAndDispose(bool callerCancels)
    {
        var clock = new ManualDeadline();
        await using var host = await new RpcFactory(clock: clock).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var body = new StalledStream();
        using var client = Client((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        var originalTimeout = client.Timeout;
        var call = Invoke<Res>(host, client, cancellation.Token);
        await body.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (callerCancels) await cancellation.CancelAsync();
        clock.Fire();
        if (callerCancels) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        else Assert.Equal(ResStatus.GatewayTimeout, (await call).Status);
        Assert.True(body.Disposed);
        Assert.Equal(originalTimeout, client.Timeout);
    }

    [Fact]
    public async Task Invoke_WhenConfiguredLimitIsLarge_ShouldStillAcceptSmallResponses()
    {
        await using var host = await new RpcFactory(maxBytes: long.MaxValue).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client((_, _) => Task.FromResult(Response(200, """{"code":200,"message":""}""")));
        Assert.True((await Invoke<Res>(host, client)).IsOk());
    }

    [Fact]
    public async Task Registration_WhenRpcClientIsOwned_ShouldLeaveUnrelatedClientsAndProviderTimeoutsIsolated()
    {
        await using var host = await new RpcFactory(registerClient: true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var scope = host.Services.CreateScope();
        Assert.Equal(Timeout.InfiniteTimeSpan, scope.ServiceProvider.GetRequiredService<IProbeRpcApi>().Timeout);
        using var unrelated = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("unrelated");
        Assert.Equal(TimeSpan.FromSeconds(100), unrelated.Timeout);
    }

    [Theory]
    [InlineData("http", false, "dependency.failed")]
    [InlineData("test-provider", false, "dependency.unavailable")]
    [InlineData("test-provider", true, "domain.failed")]
    public async Task Invoke_WhenProviderClassifierIsRegistered_ShouldUseOnlySelectedTransportAfterValidEnvelope(
        string transport, bool applicationEnvelope, string expectedCode)
    {
        await using var host = await new RpcFactory(classifier: true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var body = applicationEnvelope
            ? """{"code":500,"message":"Domain failed","metadata":{"error":{"code":"domain.failed","traceId":"origin"}}}"""
            : """{"providerError":"unavailable"}""";
        using var client = Client((_, _) => Task.FromResult(Response(500, body)));
        var result = await host.Services.GetRequiredService<IRemoteCallClient>().InvokeAsync<Res>(client,
            _ => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "http://target/")),
            Call with { Transport = transport }, TestContext.Current.CancellationToken);
        Assert.Equal(expectedCode, Error(host, result).Code);
    }

    [Fact]
    public async Task Invoke_WhenBorrowedClientSendTimesOut_ShouldPreserveItsTimeoutAndDisposeRequest()
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var requestBody = new MemoryStream([1, 2, 3]);
        using var client = Client((_, _) => throw new TaskCanceledException("send timeout", new TimeoutException()));
        var configuredTimeout = client.Timeout;
        var result = await host.Services.GetRequiredService<IRemoteCallClient>().InvokeAsync<Res>(client,
            _ => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "http://target/") { Content = new StreamContent(requestBody) }),
            Call, TestContext.Current.CancellationToken);
        Assert.Equal("dependency.timeout", Error(host, result).Code);
        Assert.Equal(configuredTimeout, client.Timeout);
        Assert.False(requestBody.CanRead);
    }

    [Fact]
    public async Task Invoke_WhenRequestFactoryHasLocalDefect_ShouldPropagateItAndNeverSend()
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var sends = 0;
        using var client = Client((_, _) => { sends++; throw new Exception("must not send"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Services.GetRequiredService<IRemoteCallClient>()
            .InvokeAsync<Res>(client, _ => throw new InvalidOperationException("local"), Call, TestContext.Current.CancellationToken));
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Invoke_WhenCustomEnvelopeUsesConstructorAndAlias_ShouldDecodeOrUseItsFailureFactory()
    {
        await using var host = await new RpcFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var good = Client((_, _) => Task.FromResult(Response(200, """{"code":200,"message":"","data":"payload"}""")));
        var result = await Invoke<CustomEnvelope>(host, good);
        Assert.Equal("payload", result.Data);
        using var bad = Client((_, _) => Task.FromResult(Response(502, "bad")));
        var failure = await Invoke<CustomEnvelope>(host, bad);
        Assert.Equal("failure-data", failure.Data);
        Assert.Equal(ResStatus.BadGateway, failure.Status);
    }

    private static Task<T> Invoke<T>(MonicaTestApplication host, HttpClient client, CancellationToken? token = null)
        where T : class, IRemoteResultEnvelope<T> =>
        host.Services.GetRequiredService<IRemoteCallClient>().InvokeAsync<T>(
            client, _ => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "http://target/orders?secret-query")),
            Call, token ?? TestContext.Current.CancellationToken);

    private static ResultError Error(MonicaTestApplication host, IResultEnvelope result)
    {
        Assert.True(result.TryGetError(host.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions, out var error));
        return error!;
    }

    private static HttpResponseMessage Response(int status, string json) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { Timeout = TimeSpan.FromSeconds(29) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed record CustomEnvelope(string Data) : IRemoteResultEnvelope<CustomEnvelope>
    {
        public string? Message { get; set; }
        public ResStatus Status { get; set; }
        public ExpandoObject? Metadata { get; set; }
        public static CustomEnvelope CreateRemoteFailure(ResStatus status, string message) =>
            new("failure-data") { Status = status, Message = message };
    }

    private sealed class RpcFactory(long maxBytes = 16384, TimeProvider? clock = null, RecordingLogs? logs = null,
        bool registerClient = false, bool classifier = false, bool exposeDiagnostics = false,
        JsonIgnoreCondition ignoreCondition = JsonIgnoreCondition.Never)
        : MonicaTestApplicationFactory<RemoteCallClientTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddJsonSerialization(options => options.DefaultIgnoreCondition = ignoreCondition);
            builder.AddResultEnvelope(options => options.ExposeDiagnosticDetails = exposeDiagnostics)
                .UseResultFieldNames(names => names.Status = "code");
            var rpc = builder.AddRpcClient(options => options.MaxResponseBodyBytes = maxBytes)
                .ConfigDomainInfoProvider(new Domains(registerClient)).ConfigHttpClientRegisterProvider<HttpProvider>();
            if (classifier) rpc.ConfigResponseClassifier<TestProviderClassifier>("test-provider");
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            // The registration scenario inspects the real factory without sending any HTTP requests.
            if (!registerClient) base.ConfigureServices(services);
            if (clock is not null) services.AddSingleton(clock);
            if (logs is not null) services.AddLogging(logging => logging.AddProvider(logs));
        }
    }

    [Flags] private enum Domain { None = 0, Orders = 1 }
    private sealed class HttpProvider : IRpcHttpClientRegisterProvider
    {
        public void ConfigureHttpClientFactoryOptions(Microsoft.Extensions.Http.HttpClientFactoryOptions options, string appId) =>
            options.HttpClientActions.Add(client => client.Timeout = TimeSpan.FromSeconds(17));
    }
    private sealed class Domains(bool enabled) : IRpcClientDomainInfoProvider
    {
        public object GetDependencyDomains() => enabled ? Domain.Orders : Domain.None;
        public RpcServiceDescriptor GetDomain(Enum domain) => Call.Service;
    }

    private sealed class TestProviderClassifier : IRemoteResponseClassifier
    {
        public string Transport => "test-provider";
        public bool TryClassify(RemoteResponseContext response,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RemoteCallFailure? failure)
        {
            failure = new RemoteCallFailure(ResStatus.ServiceUnavailable, ResultErrorCodes.DependencyUnavailable, "provider");
            return true;
        }
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }
        private sealed class RecordingLogger(string category, List<string> entries) : ILogger
        {
            public bool IsEnabled(LogLevel level) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category.EndsWith("RemoteCallFacade", StringComparison.Ordinal)) entries.Add(formatter(state, exception));
            }
        }
    }

    private sealed class ManualDeadline : TimeProvider
    {
        private Action? _fire;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _fire = () => callback(state);
            return new Timer();
        }
        public void Fire() => _fire!();
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public interface IProbeRpcApi : IRpcApi
{
    TimeSpan Timeout { get; }
}

[RpcClientDomain("Orders")]
public sealed class ProbeRpcApi(ICachedServiceProvider services, HttpClient client, RpcServiceDescriptor descriptor)
    : HttpRpcApi(services, client), IProbeRpcApi
{
    public TimeSpan Timeout => HttpClient.Timeout;
    public string ServiceName => descriptor.Name;
}
