using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.ExceptionHandling;

public sealed class ModuleExceptionHandlingIntegrationTests
{
    private const string READABLE_DIAGNOSTIC_MESSAGE = """航班保存失败：Func`1 <Flight> "引用" 路径 C:\logs\u0060.txt""";

    [Fact]
    public async Task MissingRequiredJsonMember_InProduction_ReturnsStructuredBadRequest()
    {
        var endpointInvoked = false;
        await using var application = await StartApplicationAsync(() => endpointInvoked = true);

        using var response = await application.GetTestClient().PostAsync(
            "/required-json",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<Res>(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        result.Should().NotBeNull();
        result!.Status.Should().Be(ResStatus.BadRequest);
        result.Message.Should().NotBeNull();
        Assert.DoesNotContain("Required properties", result.Message!, StringComparison.OrdinalIgnoreCase);
        endpointInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task UnsupportedJsonContentType_InProduction_ReturnsStructuredUnsupportedMediaType()
    {
        var endpointInvoked = false;
        await using var application = await StartApplicationAsync(() => endpointInvoked = true);

        using var response = await application.GetTestClient().PostAsync(
            "/required-json",
            new StringContent("{}", Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<Res>(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        result.Should().NotBeNull();
        result!.Status.Should().Be(ResStatus.UnsupportedMediaType);
        result.Message.Should().NotBeNullOrWhiteSpace();
        endpointInvoked.Should().BeFalse();
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge, ResStatus.PayloadTooLarge)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, ResStatus.UnsupportedMediaType)]
    public async Task EmptyFrameworkRejection_InProduction_ReturnsStructuredResponse(
        int statusCode,
        ResStatus expectedStatus)
    {
        await using var application = await StartApplicationAsync(static () => { });

        using var response = await application.GetTestClient().GetAsync(
            $"/empty-rejection/{statusCode}",
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<Res>(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(statusCode);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        result.Should().NotBeNull();
        result!.Status.Should().Be(expectedStatus);
        result.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnexpectedException_InProduction_ReturnsSafeErrorWithoutExceptionDetails()
    {
        await using var application = await StartApplicationAsync(static () => { });

        using var response = await application.GetTestClient().GetAsync(
            "/throw", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        raw.Should().Contain("\"code\":\"internal.unexpected\"");
        raw.Should().NotContain("secret-token");
        raw.Should().NotContain("InvalidOperationException");
        raw.Should().NotContain("stackTrace");
        raw.Should().NotContain("\"diagnostics\"");
    }

    [Fact]
    public async Task UnexpectedException_WhenDetailsEnabled_ResponseCarriesExceptionDiagnostics()
    {
        await using var application = await StartApplicationAsync(static () => { }, includeExceptionDetails: true);

        using var response = await application.GetTestClient().GetAsync(
            "/throw", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        raw.Should().Contain("\"code\":\"internal.unexpected\"");
        raw.Should().Contain("secret-token");
        raw.Should().Contain("InvalidOperationException");
        using var document = JsonDocument.Parse(raw);
        var metadata = document.RootElement.GetProperty("metadata");
        metadata.TryGetProperty("exception", out _).Should().BeFalse();
        var diagnostics = metadata.GetProperty("diagnostics");
        diagnostics.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        var entry = Assert.Single(diagnostics.GetProperty("exceptions").EnumerateArray());
        entry.GetProperty("id").GetString().Should().Be(diagnostics.GetProperty("exceptionId").GetString());
        entry.GetProperty("message").GetString().Should().Be("secret-token failure");
        diagnostics.GetProperty("request").GetProperty("path").GetString().Should().Be("/throw");
    }

    [Fact]
    public async Task UnexpectedException_WhenDetailsEnabled_ShouldKeepUnicodeAndDiagnosticSymbolsReadableInJson()
    {
        await using var application = await StartApplicationAsync(static () => { }, includeExceptionDetails: true);

        using var response = await application.GetTestClient().GetAsync(
            "/throw-readable", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        raw.Should().Contain("航班保存失败");
        raw.Should().Contain("Func`1 <Flight>");
        raw.Should().Contain(@"C:\\logs\\u0060.txt");
        raw.Should().NotContain(@"\u822A");
        raw.Should().NotContain(@"\u003CFlight\u003E");
        raw.Should().NotContain(@"Func\u00601");
        using var document = JsonDocument.Parse(raw);
        var entry = Assert.Single(document.RootElement.GetProperty("metadata")
            .GetProperty("diagnostics").GetProperty("exceptions").EnumerateArray());
        entry.GetProperty("message").GetString().Should().Be(READABLE_DIAGNOSTIC_MESSAGE);
    }

    [Fact]
    public async Task UnexpectedException_WhenDetailsArePrivate_ShouldHideReadableDiagnosticsAndKeepSafeCorrelation()
    {
        await using var application = await StartApplicationAsync(static () => { });

        using var response = await application.GetTestClient().GetAsync(
            "/throw-readable", TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        raw.Should().NotContain("航班保存失败");
        raw.Should().NotContain("Func`1");
        raw.Should().NotContain("<Flight>");
        raw.Should().NotContain(@"\\u0060.txt");
        using var document = JsonDocument.Parse(raw);
        var metadata = document.RootElement.GetProperty("metadata");
        metadata.TryGetProperty("diagnostics", out _).Should().BeFalse();
        metadata.TryGetProperty("exception", out _).Should().BeFalse();
        metadata.TryGetProperty("chain", out _).Should().BeFalse();
        var error = metadata.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("internal.unexpected");
        error.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnexpectedException_InProduction_LogStillCarriesTheExceptionObject()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using var application = await StartApplicationAsync(static () => { }, loggerProvider: loggerProvider);

        using var response = await application.GetTestClient().GetAsync(
            "/throw", TestContext.Current.CancellationToken);
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        loggerProvider.Entries.Should().Contain(entry =>
            entry.Category == typeof(ExceptionHandlerService).FullName &&
            entry.Level == LogLevel.Error);
        loggerProvider.Entries.Any(entry => entry.Exception is InvalidOperationException
            { Message: "secret-token failure" }).Should().BeTrue();
    }

    [Fact]
    public async Task UnexpectedException_InvokesRegisteredResponseDiagnosticsWithTheResponse()
    {
        var diagnostics = new RecordingDiagnostics();
        await using var application = await StartApplicationAsync(static () => { }, diagnostics: diagnostics);

        using var response = await application.GetTestClient().GetAsync(
            "/throw", TestContext.Current.CancellationToken);
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        diagnostics.Calls.Should().HaveCount(1);
        diagnostics.Calls[0].Response.Status.Should().Be(ResStatus.InternalError);
        diagnostics.Calls[0].HttpContext.Should().NotBeNull();
    }

    private static async Task<WebApplication> StartApplicationAsync(Action onEndpointInvoked,
        bool includeExceptionDetails = false, CapturingLoggerProvider? loggerProvider = null,
        RecordingDiagnostics? diagnostics = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseTestServer();
        if (loggerProvider is not null) builder.Logging.AddProvider(loggerProvider);
        if (diagnostics is not null) builder.Services.AddSingleton<IExceptionResponseDiagnostics>(diagnostics);
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(ModuleExceptionHandlingIntegrationTests).Assembly));
            monica.AddExceptionHandling();
            if (includeExceptionDetails) monica.AddResultEnvelope(options => options.ExposeDiagnosticDetails = true);
        });

        var application = builder.Build();
        application.UseMonica();
        application.MapPost("/required-json", (RequiredJsonRequest _) =>
        {
            onEndpointInvoked();
            return Microsoft.AspNetCore.Http.Results.Ok();
        }).WithMonicaEndpoint();
        application.MapGet(
            "/empty-rejection/{statusCode:int}",
            (int statusCode) => Microsoft.AspNetCore.Http.Results.StatusCode(statusCode)).WithMonicaEndpoint();
        application.MapGet("/throw", (HttpContext _) => throw new InvalidOperationException("secret-token failure"))
            .WithMonicaEndpoint();
        application.MapGet("/throw-readable", (HttpContext _) => throw new InvalidOperationException(READABLE_DIAGNOSTIC_MESSAGE))
            .WithMonicaEndpoint();
        application.MapMonica();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private sealed class RecordingDiagnostics : IExceptionResponseDiagnostics
    {
        public List<(HttpContext? HttpContext, Res Response)> Calls { get; } = [];

        public void Attach(HttpContext? httpContext, IResultEnvelope response)
        {
            Calls.Add((httpContext, (Res)response));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string? Category, LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            IDisposable? ILogger.BeginScope<TState>(TState state) { return null; }

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((category, logLevel, exception));
        }
    }
}

public sealed record RequiredJsonRequest
{
    public required string RequiredValue { get; init; }
}
