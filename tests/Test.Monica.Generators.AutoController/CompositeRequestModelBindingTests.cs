using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.WebApi.Annotations;
using Monica.Application.Annotations;
using Monica.WebApi.AutoControllers.ModelBinding;
using Monica.WebApi.AutoControllers.Services.Support;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class CompositeRequestModelBindingTests
{
    [Fact]
    public async Task BindModel_WhenGetUsesQueryAndRoute_ShouldComposeBothSources()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        var request = await client.GetFromJsonAsync<QueryRouteRequest>(
            "/composite-binding/query/17?Filter=active",
            TestContext.Current.CancellationToken);

        request.Should().Be(new QueryRouteRequest(17, "active"));
    }

    [Fact]
    public async Task BindModel_WhenBodyContainsInvalidRouteProperty_ShouldOverlayBeforeValidation()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/composite-binding/body/23",
            new BodyRouteRequest { Id = -1, Name = "payload" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = await response.Content.ReadFromJsonAsync<BodyRouteRequest>(
            TestContext.Current.CancellationToken);
        request.Should().BeEquivalentTo(new BodyRouteRequest { Id = 23, Name = "payload" });
    }

    [Fact]
    public async Task BindModel_WhenRequestIsPositionalRecord_ShouldReconstructImmutableContract()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/composite-binding/immutable/29",
            new ImmutableRouteRequest(0, "record"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = await response.Content.ReadFromJsonAsync<ImmutableRouteRequest>(
            TestContext.Current.CancellationToken);
        request.Should().Be(new ImmutableRouteRequest(29, "record"));
    }

    [Fact]
    public async Task BindModel_WhenPayloadAndRouteConflict_ShouldPreferRouteValue()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();

        var request = await client.GetFromJsonAsync<QueryRouteRequest>(
            "/composite-binding/query/41?Id=7&Filter=conflict",
            TestContext.Current.CancellationToken);

        request.Should().Be(new QueryRouteRequest(41, "conflict"));
    }

    [Fact]
    public async Task BindModel_WhenEndpointUsesForm_ShouldComposeFormAndRouteValues()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [nameof(FormRouteRequest.Id)] = "5",
            [nameof(FormRouteRequest.Label)] = "form"
        });

        using var response = await client.PostAsync(
            "/composite-binding/form/31",
            content,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = await response.Content.ReadFromJsonAsync<FormRouteRequest>(
            TestContext.Current.CancellationToken);
        request.Should().BeEquivalentTo(new FormRouteRequest { Id = 31, Label = "form" });
    }

    [Fact]
    public async Task BindModel_WhenEndpointUsesMultipartForm_ShouldBindFileAndOverlayRouteValue()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("5"), nameof(MultipartRouteRequest.Id));
        content.Add(new StringContent("manifest"), nameof(MultipartRouteRequest.Label));
        content.Add(
            new ByteArrayContent(Encoding.UTF8.GetBytes("flight-data")),
            nameof(MultipartRouteRequest.File),
            "flight.txt");

        using var response = await client.PostAsync(
            "/composite-binding/multipart/37",
            content,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<MultipartBindingResult>(
            TestContext.Current.CancellationToken);
        result.Should().Be(new MultipartBindingResult(37, "manifest", "flight.txt", 11));
    }

    [Fact]
    public async Task DescribeApi_WhenCompositeRequestOwnsRouteProperty_ShouldExposeOneTypedPathParameter()
    {
        await using var application = await CreateApplicationAsync();
        var descriptions = application.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(static group => group.Items);

        var queryDescription = descriptions.Single(description =>
            description.ActionDescriptor.RouteValues["action"] == nameof(CompositeRequestModelBindingController.GetQuery));
        var routeParameter = queryDescription.ParameterDescriptions.Single(parameter =>
            string.Equals(parameter.Name, nameof(QueryRouteRequest.Id), StringComparison.OrdinalIgnoreCase));

        routeParameter.Source.Should().Be(BindingSource.Path);
        routeParameter.Type.Should().Be(typeof(int));
        routeParameter.ModelMetadata.ModelType.Should().Be(typeof(int));
        routeParameter.IsRequired.Should().BeTrue();
        queryDescription.ParameterDescriptions.Should().ContainSingle(parameter =>
            parameter.Source == BindingSource.Query &&
            parameter.Name == nameof(QueryRouteRequest.Filter) &&
            parameter.Type == typeof(string));
    }

    [Fact]
    public async Task DescribeApi_WhenBodyRequestOwnsRouteProperty_ShouldKeepBodyAndTypedPathMetadata()
    {
        await using var application = await CreateApplicationAsync();
        var descriptions = application.Services
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(static group => group.Items);

        var bodyDescription = descriptions.Single(description =>
            description.ActionDescriptor.RouteValues["action"] == nameof(CompositeRequestModelBindingController.PostBody));

        bodyDescription.ParameterDescriptions.Should().ContainSingle(parameter =>
            parameter.Source == BindingSource.Body && parameter.Type == typeof(BodyRouteRequest));
        bodyDescription.ParameterDescriptions.Should().ContainSingle(parameter =>
            parameter.Name == nameof(BodyRouteRequest.Id) &&
            parameter.Source == BindingSource.Path &&
            parameter.Type == typeof(int));
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(CompositeRequestModelBindingController).Assembly);
        builder.Services.AddTransient<IApiDescriptionProvider, RequestEndpointApiDescriptionProvider>();

        var application = builder.Build();
        application.MapControllers();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }
}

[ApiController]
[Route("composite-binding")]
public sealed class CompositeRequestModelBindingController : ControllerBase
{
    [HttpGet("query/{Id}")]
    public QueryRouteRequest GetQuery(
        [ApiEndpointRequest(ApiRequestBinding.Query)] QueryRouteRequest request)
    {
        return request;
    }

    [HttpPost("body/{Id}")]
    public BodyRouteRequest PostBody(
        [ApiEndpointRequest(ApiRequestBinding.Body)] BodyRouteRequest request)
    {
        return request;
    }

    [HttpPost("immutable/{Id}")]
    public ImmutableRouteRequest PostImmutable(
        [ApiEndpointRequest(ApiRequestBinding.Body)] ImmutableRouteRequest request)
    {
        return request;
    }

    [HttpPost("form/{Id}")]
    public FormRouteRequest PostForm(
        [ApiEndpointRequest(ApiRequestBinding.Form)] FormRouteRequest request)
    {
        return request;
    }

    [HttpPost("multipart/{Id}")]
    public MultipartBindingResult PostMultipart(
        [ApiEndpointRequest(ApiRequestBinding.Form)] MultipartRouteRequest request)
    {
        return new MultipartBindingResult(
            request.Id,
            request.Label,
            request.File.FileName,
            request.File.Length);
    }
}

/// <summary>
/// Query request used to verify query and route composition.
/// </summary>
[ApiEndpoint(ApiHttpMethod.Get, "query/{Id}", Binding = ApiRequestBinding.Query)]
public sealed record QueryRouteRequest(int Id, string Filter);

/// <summary>
/// Body request used to verify route overlay before validation.
/// </summary>
[ApiEndpoint(ApiHttpMethod.Post, "body/{Id}", Binding = ApiRequestBinding.Body)]
public sealed class BodyRouteRequest
{
    [Range(1, int.MaxValue)]
    public int Id { get; init; }

    [Required]
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Positional record used to verify immutable request reconstruction.
/// </summary>
[ApiEndpoint(ApiHttpMethod.Post, "immutable/{Id}", Binding = ApiRequestBinding.Body)]
public sealed record ImmutableRouteRequest(int Id, string Name);

/// <summary>
/// Form request used to verify form and route composition.
/// </summary>
[ApiEndpoint(ApiHttpMethod.Post, "form/{Id}", Binding = ApiRequestBinding.Form)]
public sealed class FormRouteRequest
{
    public int Id { get; init; }

    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Multipart request used to verify file binding and route overlay.
/// </summary>
[ApiEndpoint(ApiHttpMethod.Post, "multipart/{Id}", Binding = ApiRequestBinding.Form)]
public sealed class MultipartRouteRequest
{
    public int Id { get; init; }

    public string Label { get; init; } = string.Empty;

    public IFormFile File { get; init; } = null!;
}

public sealed record MultipartBindingResult(
    int Id,
    string Label,
    string FileName,
    long Length);
