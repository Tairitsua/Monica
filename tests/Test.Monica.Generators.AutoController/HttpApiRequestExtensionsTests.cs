using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Monica.Core.JsonSerialization.Models;
using Monica.Invocation.Extensions;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class HttpApiRequestExtensionsTests
{
    [Fact]
    public void BuildApiRequestUri_WhenOptionalRouteValueIsNull_ShouldRemoveSegmentAndExcludePropertyFromQuery()
    {
        var request = new OptionalRouteRequest(null, "active");

        var uri = request.BuildApiRequestUri(
            "api/orders/{Id?}",
            includeQueryString: true,
            DateTimeWireFormat.Iso8601WallClock);

        uri.Should().Be("api/orders?filter=active");
    }

    [Fact]
    public void BuildApiRequestUri_WhenNullRouteValueHasDefault_ShouldUseDefaultAndExcludePropertyFromQuery()
    {
        var request = new DefaultRouteRequest(null, "active");

        var uri = request.BuildApiRequestUri(
            "api/orders/{Culture=en-US}",
            includeQueryString: true,
            DateTimeWireFormat.Iso8601WallClock);

        uri.Should().Be("api/orders/en-US?filter=active");
    }

    [Fact]
    public void BuildApiRequestUri_WhenCatchAllHasValue_ShouldEscapeValueAndExcludePropertyFromQuery()
    {
        var request = new CatchAllRouteRequest("folder A/child", "active");

        var uri = request.BuildApiRequestUri(
            "api/files/{**Path}",
            includeQueryString: true,
            DateTimeWireFormat.Iso8601WallClock);

        uri.Should().Be("api/files/folder%20A%2Fchild?filter=active");
    }

    [Fact]
    public void BuildApiRequestUri_WhenRequiredRouteValueIsNull_ShouldFailClearly()
    {
        var request = new RequiredRouteRequest(null);

        var build = () => request.BuildApiRequestUri(
            "api/orders/{Id}",
            includeQueryString: false,
            DateTimeWireFormat.Iso8601WallClock);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*Route property 'Id'*");
    }

    [Fact]
    public void BuildApiRequestUri_WhenQueryContainsTemporalValues_ShouldPreserveTheirWireSemantics()
    {
        var wallClock = new DateTime(2026, 7, 28, 14, 30, 0).AddTicks(1_234_567);
        var request = new TemporalQueryRequest(
            DateTime.SpecifyKind(wallClock, DateTimeKind.Local),
            DateTime.SpecifyKind(wallClock, DateTimeKind.Utc),
            DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(wallClock, DateTimeKind.Local),
            null,
            new DateTimeOffset(wallClock, TimeSpan.FromHours(8)),
            [wallClock, wallClock.AddTicks(1)]);

        var uri = request.BuildApiRequestUri(
            "api/events",
            includeQueryString: true,
            DateTimeWireFormat.Iso8601WallClock);
        var query = QueryHelpers.ParseQuery(new Uri($"https://localhost/{uri}").Query);

        const string expectedWallClock = "2026-07-28T14:30:00.1234567";
        query["local"].ToString().Should().Be(expectedWallClock);
        query["utc"].ToString().Should().Be(expectedWallClock);
        query["unspecified"].ToString().Should().Be(expectedWallClock);
        query["nullable"].ToString().Should().Be(expectedWallClock);
        query.Should().NotContainKey("missing");
        query["instant"].ToString().Should().Be("2026-07-28T14:30:00.1234567+08:00");
        query["samples"].Should().Equal(
            expectedWallClock,
            "2026-07-28T14:30:00.1234568");
    }

    [Fact]
    public void BuildApiRequestUri_WhenRouteContainsDateTime_ShouldUseCanonicalWallClockFormat()
    {
        var request = new TemporalRouteRequest(
            new DateTime(2026, 7, 28, 14, 30, 0, DateTimeKind.Utc).AddTicks(1_234_567));

        var uri = request.BuildApiRequestUri(
            "api/events/{OccurredAt}",
            includeQueryString: false,
            DateTimeWireFormat.Iso8601WallClock);

        uri.Should().Be("api/events/2026-07-28T14%3A30%3A00.1234567");
    }

    [Fact]
    public void BuildApiRequestUri_WhenSpaceSeparatedFormatIsSelected_ShouldApplyItToQueriesAndCollections()
    {
        var value = new DateTime(2026, 7, 28, 14, 30, 0).AddTicks(1_234_567);
        var request = new TemporalQueryRequest(value, value, value, value, null, new DateTimeOffset(value),
            [value, value.AddTicks(1)]);

        var uri = request.BuildApiRequestUri(
            "api/events",
            includeQueryString: true,
            DateTimeWireFormat.SpaceSeparatedWallClock);
        var query = QueryHelpers.ParseQuery(new Uri($"https://localhost/{uri}").Query);

        query["local"].ToString().Should().Be("2026-07-28 14:30:00.1234567");
        query["samples"].Should().Equal(
            "2026-07-28 14:30:00.1234567",
            "2026-07-28 14:30:00.1234568");
    }

    [Fact]
    public void BuildApiRequestUri_WhenSpaceSeparatedFormatIsSelected_ShouldApplyItToRoutes()
    {
        var request = new TemporalRouteRequest(
            new DateTime(2026, 7, 28, 14, 30, 0).AddTicks(1_234_567));

        var uri = request.BuildApiRequestUri(
            "api/events/{OccurredAt}",
            includeQueryString: false,
            DateTimeWireFormat.SpaceSeparatedWallClock);

        uri.Should().Be("api/events/2026-07-28%2014%3A30%3A00.1234567");
    }

    private sealed record OptionalRouteRequest(long? Id, string Filter);

    private sealed record DefaultRouteRequest(string? Culture, string Filter);

    private sealed record CatchAllRouteRequest(string Path, string Filter);

    private sealed record RequiredRouteRequest(long? Id);

    private sealed record TemporalQueryRequest(
        DateTime Local,
        DateTime Utc,
        DateTime Unspecified,
        DateTime? Nullable,
        DateTime? Missing,
        DateTimeOffset Instant,
        IReadOnlyList<DateTime> Samples);

    private sealed record TemporalRouteRequest(DateTime OccurredAt);
}
