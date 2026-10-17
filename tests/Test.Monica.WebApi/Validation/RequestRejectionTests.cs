using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Execution;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.Execution.Mvc;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Hosting;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace Test.Monica.Application.Validation;

public sealed class RequestRejectionTests
{
    [Fact]
    public async Task OpenApi_WhenUsingAlias_ShouldDescribeTypedErrorAndMappedFailures()
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var document = await host.Services.GetRequiredService<IAsyncSwaggerProvider>().GetSwaggerAsync("v1");
        var schemas = document.Components!.Schemas!.Values;
        Assert.Contains(schemas, schema => schema.Properties?.ContainsKey("code") == true &&
            schema.Properties.ContainsKey("metadata") &&
            schema.Properties["metadata"].Properties?.ContainsKey("error") == true);
        Assert.Contains(schemas, schema => schema.Required?.Contains("traceId") == true && schema.Required.Contains("code"));
        var operation = document.Paths["/ingress/api/body"].Operations!.Values.Single();
        Assert.Contains("503", operation.Responses!.Keys);
        Assert.Contains("415", operation.Responses.Keys);
    }

    [Theory]
    [InlineData("/ingress/api/body", "{\"when\":\"secret-invalid-date\"}", "application/json", 400, 400)]
    [InlineData("/ingress/crud/body", "{\"when\":\"secret-invalid-date\"}", "application/json", 400, 400)]
    [InlineData("/ingress/crud/body", "null", "application/json", 400, 400)]
    [InlineData("/ingress/api/body", "{}", "text/plain", 415, 415)]
    [InlineData("/ingress/crud/body", "{}", "text/plain", 415, 415)]
    [InlineData("/ingress/crud/form", "count=secret-invalid-number", "application/x-www-form-urlencoded", 400, 400)]
    public async Task Post_WhenBindingFails_ShouldRejectBeforePipelineAndAction(
        string route, string body, string mediaType, int httpStatus, int resultStatus)
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client(host);
        using var response = await client.PostAsync(route, new StringContent(body, Encoding.UTF8, mediaType), TestContext.Current.CancellationToken);
        await AssertRejection(host, response, httpStatus, resultStatus);
    }

    [Theory]
    [InlineData("/ingress/crud/query?count=secret-invalid-number")]
    [InlineData("/ingress/crud/header")]
    [InlineData("/ingress/crud/route/secret-invalid-number")]
    public async Task Get_WhenSuppliedValueIsInvalid_ShouldRejectBeforePipelineAndAction(string route)
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client(host);
        client.DefaultRequestHeaders.Add("X-Count", "secret-invalid-number");
        using var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
        await AssertRejection(host, response, 400, 400);
    }

    [Fact]
    public async Task Get_WhenOptionalQueryIsOmitted_ShouldExecuteNormally()
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client(host);
        using var response = await client.GetAsync("/ingress/crud/optional", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, host.Services.GetRequiredService<ExecutionCounts>().Actions);
        Assert.Equal(1, host.Services.GetRequiredService<ExecutionCounts>().Pipeline);
    }

    [Fact]
    public async Task Get_WhenActionCreatesResourceWithNullData_ShouldProject201()
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client(host);
        using var response = await client.GetAsync("/ingress/crud/created", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(201, json.GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("/ingress/minimal", "{\"name\":null}", 400)]
    [InlineData("/ingress/minimal", "{\"name\":\"valid\",\"quantity\":0}", 400)]
    [InlineData("/ingress/minimal", "{\"quantity\":\"secret\"}", 400)]
    public async Task Minimal_WhenBindingOrAddValidationRejects_ShouldUseTypedEnvelope(
        string route, string body, int httpStatus)
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client(host);
        using var response = await client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        await AssertRejection(host, response, httpStatus, 400);
    }

    [Fact]
    public async Task Minimal_WhenUnrelatedEndpointValidates_ShouldRetainProblemDetails()
    {
        await using var host = await new ApiFactory().CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/unrelated", new { name = (string?)null }, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(400, json.GetProperty("status").GetInt32());
        Assert.False(json.TryGetProperty("code", out _));
    }

    private static HttpClient Client(MonicaTestApplication host) =>
        ((TestServer)host.Services.GetRequiredService<IServer>()).CreateClient();

    private static async Task AssertRejection(MonicaTestApplication host, HttpResponseMessage response, int http, int status)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(http == (int)response.StatusCode, host.Services.GetRequiredService<CapturedException>().Exception?.ToString() ?? text);
        Assert.DoesNotContain("secret", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(status, json.RootElement.GetProperty("code").GetInt32());
        var error = json.RootElement.GetProperty("metadata").GetProperty("error");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));
        Assert.Contains(error.GetProperty("code").GetString(), new[] { "request.invalid", "validation.failed" });
        Assert.Equal(0, host.Services.GetRequiredService<ExecutionCounts>().Actions);
        Assert.Equal(0, host.Services.GetRequiredService<ExecutionCounts>().Pipeline);
    }

    private sealed class ApiFactory : MonicaTestApplicationFactory<RequestRejectionTests>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder) => builder.Services.AddValidation();

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddResultEnvelope().UseResultFieldNames(names => names.Status = "code");
            builder.AddAutoControllers();
            builder.AddSwagger();
            builder.AddExecutionPipeline().AddBehavior<CountPipeline>(ExecutionBehaviorOrder.Routing,
                descriptor => descriptor.Point == MvcExecutionPoints.Action);
            builder.AddExceptionHandling().ConfigureEndpoints(context =>
                context.ApplicationBuilder.UseEndpoints(endpoints =>
                {
                    endpoints.MapPost("/ingress/minimal", (MinimalRequest request, ExecutionCounts counts) =>
                    {
                        counts.Actions++;
                        return Res.Ok().GetResponse();
                    }).WithMonicaEndpoint();
                    endpoints.MapPost("/unrelated", (MinimalRequest request) => Microsoft.AspNetCore.Http.Results.Ok());
                }));
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton<ExecutionCounts>();
            services.AddSingleton<CapturedException>();
            services.AddSingleton<IExceptionResponseMapper>(provider => provider.GetRequiredService<CapturedException>());
        }
    }
}

public sealed class CapturedException : IExceptionResponseMapper
{
    public Exception? Exception { get; private set; }
    public bool TryMap(HttpContext? context, Exception exception, CancellationToken cancellationToken,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Res? response)
    {
        Exception = exception;
        response = null;
        return false;
    }
}

public sealed class ExecutionCounts
{
    public int Actions { get; set; }
    public int Pipeline { get; set; }
}

public sealed class CountPipeline(ExecutionCounts counts) : IExecutionBehavior<MvcActionExecutionInput, MvcActionExecutionResult>
{
    public Task<MvcActionExecutionResult> ExecuteAsync(ExecutionContext<MvcActionExecutionInput> context,
        ExecutionDelegate<MvcActionExecutionResult> next)
    {
        counts.Pipeline++;
        return next();
    }
}

public sealed record MinimalRequest([Required] string Name, [Range(1, 100)] int Quantity);
public sealed record BodyRequest(DateTime When);

[ApiController, Route("ingress/api")]
public sealed class IngressApiController(ExecutionCounts counts) : ControllerBase
{
    [HttpPost("body")]
    public Res Body([FromBody] BodyRequest request) { counts.Actions++; return Res.Ok(); }
}

[Route("ingress/crud")]
public sealed class IngressCrudController(ExecutionCounts counts) : ControllerBase
{
    [HttpPost("body")]
    public Res Body([FromBody] BodyRequest request) => Success();
    [HttpPost("form")]
    public Res Form([FromForm] int count) => Success();
    [HttpGet("query")]
    public Res Query([FromQuery] int count) => Success();
    [HttpGet("header")]
    public Res Header([FromHeader(Name = "X-Count")] int count) => Success();
    [HttpGet("route/{id}")]
    public Res RouteValue([FromRoute] int id) => Success();
    [HttpGet("optional")]
    public Res Optional([FromQuery] int? count) => Success();
    [HttpGet("created")]
    public Res<string?> CreatedValue() => new("", ResStatus.Created);

    private Res Success() { counts.Actions++; return Res.Ok(); }
}
