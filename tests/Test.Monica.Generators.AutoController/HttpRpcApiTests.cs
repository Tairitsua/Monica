using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.JsonSerialization.Converters;
using Monica.Core.JsonSerialization.Models;
using Monica.Core.JsonSerialization.Services;
using Monica.DependencyInjection.Abstractions;
using Monica.DependencyInjection.Services;
using Monica.Rpc.Abstractions;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class HttpRpcApiTests
{
    [Fact]
    public async Task CreateJsonRequestContent_WhenHostOwnsSerializerOptions_ShouldUseThoseOptions()
    {
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        serializerOptions.Converters.Add(new DateTimeJsonConverter(DateTimeWireFormat.Iso8601WallClock));

        await using var serviceProvider = new ServiceCollection()
            .AddSingleton<IJsonSerializerOptionsProvider>(
                new JsonSerializerOptionsProvider(
                    serializerOptions,
                    DateTimeWireFormat.Iso8601WallClock))
            .BuildServiceProvider();
        using var httpClient = new HttpClient();
        var api = new ExposedHttpRpcApi(new CachedServiceProvider(serviceProvider), httpClient);
        var request = new BodyRequest(
            new DateTime(2026, 7, 28, 14, 30, 0, DateTimeKind.Utc).AddTicks(1_234_567));

        using var content = api.CreateRequestContent(request);
        var json = await content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        json.Should().Be("{\"occurredAt\":\"2026-07-28T14:30:00.1234567\"}");
    }

    [Fact]
    public async Task HostOwnedDateTimeFormat_ShouldApplyConsistentlyToJsonBodiesAndRequestUris()
    {
        var dateTimeFormat = DateTimeWireFormat.SpaceSeparatedWallClock;
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        serializerOptions.Converters.Add(new DateTimeJsonConverter(dateTimeFormat));

        await using var serviceProvider = new ServiceCollection()
            .AddSingleton<IJsonSerializerOptionsProvider>(
                new JsonSerializerOptionsProvider(serializerOptions, dateTimeFormat))
            .BuildServiceProvider();
        using var httpClient = new HttpClient();
        var api = new ExposedHttpRpcApi(new CachedServiceProvider(serviceProvider), httpClient);
        var value = new DateTime(2026, 7, 28, 14, 30, 0).AddTicks(1_234_567);

        using var content = api.CreateRequestContent(new BodyRequest(value));
        var json = await content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var uri = api.CreateUri(
            new QueryRequest(value, [value, value.AddTicks(1)]),
            "api/events",
            includeQueryString: true);
        var query = QueryHelpers.ParseQuery(new Uri($"https://localhost/{uri}").Query);

        json.Should().Be("{\"occurredAt\":\"2026-07-28 14:30:00.1234567\"}");
        query["occurredAt"].ToString().Should().Be("2026-07-28 14:30:00.1234567");
        query["samples"].Should().Equal(
            "2026-07-28 14:30:00.1234567",
            "2026-07-28 14:30:00.1234568");
    }

    private sealed class ExposedHttpRpcApi(
        ICachedServiceProvider serviceProvider,
        HttpClient httpClient)
        : HttpRpcApi(serviceProvider, httpClient)
    {
        public HttpContent CreateRequestContent<TRequest>(TRequest request)
        {
            return CreateJsonRequestContent(request);
        }

        public string CreateUri<TRequest>(
            TRequest request,
            string routeTemplate,
            bool includeQueryString)
        {
            return CreateRequestUri(request, routeTemplate, includeQueryString);
        }
    }

    private sealed record BodyRequest(DateTime OccurredAt);

    private sealed record QueryRequest(DateTime OccurredAt, IReadOnlyList<DateTime> Samples);
}
