using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.JsonSerialization.Models;
using Monica.Invocation.Extensions;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class DateTimeApiModelBindingTests
{
    [Fact]
    public async Task BindModel_WhenGeneratedQueryUsesCanonicalDateTime_ShouldPreserveTicksWithoutKind()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var value = new DateTime(2026, 7, 28, 14, 30, 0, DateTimeKind.Local)
            .AddTicks(1_234_567);
        var uri = new DateTimeBindingRequest(value)
            .BuildApiRequestUri(
                "date-time-rpc-binding",
                includeQueryString: true,
                DateTimeWireFormat.Iso8601WallClock);

        var result = await client.GetFromJsonAsync<DateTimeBindingResult>(
            uri,
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Ticks.Should().Be(value.Ticks);
        result.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public async Task BindModel_WhenGeneratedQueryUsesSpaceSeparatedDateTime_ShouldPreserveTicksWithoutKind()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var value = new DateTime(2026, 7, 28, 14, 30, 0, DateTimeKind.Utc)
            .AddTicks(1_234_567);
        var uri = new DateTimeBindingRequest(value)
            .BuildApiRequestUri(
                "date-time-rpc-binding",
                includeQueryString: true,
                DateTimeWireFormat.SpaceSeparatedWallClock);

        var result = await client.GetFromJsonAsync<DateTimeBindingResult>(
            uri,
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Ticks.Should().Be(value.Ticks);
        result.Kind.Should().Be(DateTimeKind.Unspecified);
    }

    [Fact]
    public async Task BindModel_WhenGeneratedQueryUsesDateTimeOffset_ShouldPreserveOffsetAndInstant()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var value = new DateTimeOffset(2026, 7, 28, 14, 30, 0, TimeSpan.FromHours(8))
            .AddTicks(1_234_567);
        var uri = new DateTimeOffsetBindingRequest(value)
            .BuildApiRequestUri(
                "date-time-rpc-binding/offset",
                includeQueryString: true,
                DateTimeWireFormat.Iso8601WallClock);

        var result = await client.GetFromJsonAsync<DateTimeOffsetBindingResult>(
            uri,
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Offset.Should().Be(value.Offset);
        result.UtcTicks.Should().Be(value.UtcDateTime.Ticks);
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(DateTimeApiModelBindingController).Assembly);

        var application = builder.Build();
        application.MapControllers();
        await application.StartAsync(TestContext.Current.CancellationToken);
        return application;
    }

    private sealed record DateTimeBindingRequest(DateTime Value);

    private sealed record DateTimeOffsetBindingRequest(DateTimeOffset Value);
}

[ApiController]
[Route("date-time-rpc-binding")]
public sealed class DateTimeApiModelBindingController : ControllerBase
{
    [HttpGet]
    public DateTimeBindingResult Get([FromQuery] DateTime value)
    {
        return new DateTimeBindingResult(value.Ticks, value.Kind);
    }

    [HttpGet("offset")]
    public DateTimeOffsetBindingResult GetOffset([FromQuery] DateTimeOffset value)
    {
        return new DateTimeOffsetBindingResult(value.UtcDateTime.Ticks, value.Offset);
    }
}

public sealed record DateTimeBindingResult(long Ticks, DateTimeKind Kind);

public sealed record DateTimeOffsetBindingResult(long UtcTicks, TimeSpan Offset);
