using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Results;
using Monica.OpenTelemetry.InProcessCollector.Facades;
using Monica.OpenTelemetry.InProcessCollector.Models;
using Monica.Testing.Hosting;
using Test.Monica.OpenTelemetry.Support;
using Xunit;

namespace Test.Monica.OpenTelemetry.InProcessCollector.Endpoints;

/// <summary>Exercises the production snapshot endpoint and its host-owned JSON/result contract.</summary>
public sealed class OpenTelemetrySnapshotEndpointTests
{
    private const string SNAPSHOT_PATH = "/opentelemetry/snapshot";

    [Fact]
    public async Task GetSnapshot_WhenMinimalApiIsDisabled_ShouldLeaveTheProductionRouteAbsent()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateGauge<double>("retained-without-api").Record(4.5);
        var retained = await scope.Resolve<OpenTelemetryFacade>().GetSnapshotAsync();
        retained.Status.Should().Be(ResultStatus.Ok);
        retained.Message.Should().BeNullOrEmpty();
        retained.Data.Should().NotBeNull();
        retained.Data!.Instruments.Single().CurrentValue.Should().Be(4.5);
        using var client = CreateClient(application);

        using var response = await client.GetAsync(SNAPSHOT_PATH, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        GetSnapshotEndpoints(application).Should().BeEmpty();
    }

    [Fact]
    public async Task GetSnapshot_WhenGenuineMeasurementsAreNonfinite_ShouldReturnTheExistingSuccessEnvelopeAndLosslessNumericEvidence()
    {
        var factory = new OpenTelemetryTestApplicationFactory(samplesPerSeries: 6, enableMinimalApi: true);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var meter = scope.Resolve<IMeterFactory>().Create(factory.MeterName, "2.7");
        var gauge = meter.CreateGauge<double>("raw-reading", "Cel", "Literal gauge evidence: NaN, infinity, and finite values.");
        KeyValuePair<string, object?>[] gaugeTags = [new("A", "one"), new("a", "two")];
        gauge.Record(double.NaN, gaugeTags);
        gauge.Record(double.PositiveInfinity, gaugeTags);
        gauge.Record(double.NegativeInfinity, gaugeTags);
        gauge.Record(7.5, gaugeTags);
        var overflow = meter.CreateGauge<double>("aggregate-overflow", "By", "Two finite series overflow only their computed aggregate.");
        overflow.Record(double.MaxValue, new KeyValuePair<string, object?>("part", "first"));
        overflow.Record(double.MaxValue, new KeyValuePair<string, object?>("part", "second"));
        var histogram = meter.CreateHistogram<double>("mixed-window", "ms", "Finite statistics exclude retained nonfinite observations.");
        histogram.Record(double.NaN);
        histogram.Record(2);
        histogram.Record(double.PositiveInfinity);
        histogram.Record(4);
        var invalidOnly = meter.CreateHistogram<double>("invalid-window", "ms", "No finite distribution can be inferred.");
        invalidOnly.Record(double.NaN);
        invalidOnly.Record(double.PositiveInfinity);
        invalidOnly.Record(double.NegativeInfinity);
        var before = await scope.Resolve<OpenTelemetryFacade>().GetSnapshotAsync();
        before.Status.Should().Be(ResultStatus.Ok);
        before.Message.Should().BeNullOrEmpty();
        before.Data.Should().NotBeNull();
        using var client = CreateClient(application);

        using var response = await client.GetAsync(SNAPSHOT_PATH, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "response body: {0}", body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        GetSnapshotEndpoints(application).Should().ContainSingle();
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("status", "message", "data");
        json.RootElement.GetProperty("status").GetInt32().Should().Be((int)ResultStatus.Ok);
        json.RootElement.GetProperty("message").ValueKind.Should().Be(JsonValueKind.Null);
        var data = json.RootElement.GetProperty("data");
        data.GetProperty("resourceServiceName").GetString().Should().Be("OpenTelemetry behavior test host");
        data.GetProperty("samplesPerSeries").GetInt32().Should().Be(6);
        data.GetProperty("maxTagSetsPerInstrument").GetInt32().Should().Be(8);
        var instruments = data.GetProperty("instruments").EnumerateArray().ToArray();
        instruments.Should().HaveCount(4);
        var rawGauge = instruments.Single(instrument => instrument.GetProperty("name").GetString() == "raw-reading");
        var gaugeSeries = rawGauge.GetProperty("tagSets")[0];
        var tags = gaugeSeries.GetProperty("tags");
        tags.EnumerateObject().Select(tag => tag.Name).Should().BeEquivalentTo("A", "a");
        tags.GetProperty("A").GetString().Should().Be("one");
        tags.GetProperty("a").GetString().Should().Be("two");
        var values = gaugeSeries.GetProperty("samples").EnumerateArray().Select(sample => sample.GetProperty("value")).ToArray();
        values[0].ValueKind.Should().Be(JsonValueKind.String);
        values[0].GetString().Should().Be("NaN");
        values[1].GetString().Should().Be("Infinity");
        values[2].GetString().Should().Be("-Infinity");
        values[3].ValueKind.Should().Be(JsonValueKind.Number);
        values[3].GetDouble().Should().Be(7.5);
        gaugeSeries.GetProperty("currentValue").ValueKind.Should().Be(JsonValueKind.Number);
        gaugeSeries.GetProperty("currentValue").GetDouble().Should().Be(7.5);
        gaugeSeries.GetProperty("delta").GetString().Should().Be("Infinity");
        rawGauge.GetProperty("delta").GetString().Should().Be("Infinity");
        instruments.Single(instrument => instrument.GetProperty("name").GetString() == "aggregate-overflow")
            .GetProperty("currentValue").GetString().Should().Be("Infinity");
        var invalidJson = instruments.Single(instrument => instrument.GetProperty("name").GetString() == "invalid-window")
            .GetProperty("tagSets")[0];
        invalidJson.GetProperty("currentValue").GetString().Should().Be("-Infinity");
        invalidJson.GetProperty("histogram").ValueKind.Should().Be(JsonValueKind.Null);

        // Deserialization uses the exact host contract, including its existing long-as-string converter.
        var options = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        options.DictionaryKeyPolicy!.ConvertName("A").Should().Be("a", "the tag converter must preserve keys locally without changing host-wide policy");
        var envelope = JsonSerializer.Deserialize<Result<OpenTelemetrySnapshot>>(body, options)!;
        envelope.Status.Should().Be(ResultStatus.Ok);
        envelope.Message.Should().BeNullOrEmpty();
        envelope.Data.Should().NotBeNull();
        var transported = envelope.Data!;
        transported.Instruments.Should().HaveCount(4);
        transported.Instruments.Select(instrument => instrument.InstrumentId).Should().OnlyHaveUniqueItems();
        foreach (var instrument in transported.Instruments)
        {
            var original = before.Data!.Instruments.Single(candidate => candidate.InstrumentId == instrument.InstrumentId);
            instrument.InstrumentId.Should().NotBeNullOrWhiteSpace();
            instrument.MeterName.Should().Be(factory.MeterName);
            instrument.MeterVersion.Should().Be("2.7");
            instrument.Name.Should().Be(original.Name);
            instrument.Kind.Should().Be(original.Kind);
            instrument.Unit.Should().Be(original.Unit);
            instrument.Description.Should().Be(original.Description);
            instrument.IsObservable.Should().BeFalse();
            instrument.Overflow.Should().BeFalse();
            instrument.DroppedSamples.Should().Be(0);
            instrument.TagSets.Should().HaveCount(original.TagSets.Count);
            foreach (var series in instrument.TagSets)
            {
                var originalSeries = original.TagSets.Single(candidate => candidate.Key == series.Key);
                series.Tags.Should().BeEquivalentTo(originalSeries.Tags);
                series.Samples.Select(sample => sample.TimestampUtc).Should().Equal(originalSeries.Samples.Select(sample => sample.TimestampUtc));
                series.FiniteSampleCount.Should().Be(originalSeries.FiniteSampleCount);
                series.NonFiniteSampleCount.Should().Be(originalSeries.NonFiniteSampleCount);
            }
        }
        var retainedGauge = transported.Instruments.Single(instrument => instrument.Name == "raw-reading");
        retainedGauge.Kind.Should().Be("Gauge");
        var retainedGaugeSeries = retainedGauge.TagSets.Single();
        retainedGaugeSeries.Tags.Should().HaveCount(2);
        retainedGaugeSeries.Tags["A"].Should().Be("one");
        retainedGaugeSeries.Tags["a"].Should().Be("two");
        var retainedGaugeSamples = retainedGaugeSeries.Samples;
        double.IsNaN(retainedGaugeSamples[0].Value).Should().BeTrue();
        retainedGaugeSamples[1].Value.Should().Be(double.PositiveInfinity);
        retainedGaugeSamples[2].Value.Should().Be(double.NegativeInfinity);
        retainedGaugeSamples[3].Value.Should().Be(7.5);
        var retainedOverflow = transported.Instruments.Single(instrument => instrument.Name == "aggregate-overflow");
        retainedOverflow.CurrentValue.Should().Be(double.PositiveInfinity);
        retainedOverflow.TagSets.Should().AllSatisfy(series => series.CurrentValue.Should().Be(double.MaxValue));
        var mixed = transported.Instruments.Single(instrument => instrument.Name == "mixed-window").TagSets.Single();
        mixed.Samples.Should().HaveCount(4);
        double.IsNaN(mixed.Samples[0].Value).Should().BeTrue();
        mixed.Samples[2].Value.Should().Be(double.PositiveInfinity);
        mixed.FiniteSampleCount.Should().Be(2);
        mixed.NonFiniteSampleCount.Should().Be(2);
        var finiteWindow = mixed.Histogram!;
        finiteWindow.Count.Should().Be(2);
        finiteWindow.NonFiniteSampleCount.Should().Be(2);
        finiteWindow.Average.Should().Be(3);
        finiteWindow.Buckets.Sum(bucket => bucket.Count).Should().Be(2);
        var noDistribution = transported.Instruments.Single(instrument => instrument.Name == "invalid-window").TagSets.Single();
        noDistribution.Histogram.Should().BeNull();
        noDistribution.FiniteSampleCount.Should().Be(0);
        noDistribution.NonFiniteSampleCount.Should().Be(3);
    }

    private static HttpClient CreateClient(MonicaTestApplication application) =>
        ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

    private static RouteEndpoint[] GetSnapshotEndpoints(MonicaTestApplication application) =>
        application.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText == SNAPSHOT_PATH)
            .ToArray();
}
