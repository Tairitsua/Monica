using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Monica.Core.Results;
using Monica.OpenTelemetry.InProcessCollector.Facades;
using Monica.OpenTelemetry.InProcessCollector.Models;
using Monica.OpenTelemetry.InProcessCollector.Services;
using Test.Monica.OpenTelemetry.Support;
using Xunit;

namespace Test.Monica.OpenTelemetry.InProcessCollector.Services;

/// <summary>Exercises actual module/listener/facade composition through genuine .NET instruments.</summary>
public sealed class InProcessMetricsCollectorTests
{
    [Fact]
    public async Task GetSnapshotAsync_WhenInstrumentNamesMatchAcrossMetersAndVersions_ShouldKeepStableDistinctIdentities()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var meters = scope.Resolve<IMeterFactory>();
        var first = meters.Create(factory.MeterName, "1.0").CreateCounter<long>("shared", "items", "Same declared instrument metadata.");
        var versioned = meters.Create(factory.MeterName, "2.0").CreateCounter<long>("shared", "items", "Same declared instrument metadata.");
        var other = meters.Create(factory.OtherMeterName, "1.0").CreateCounter<long>("shared", "items", "Same declared instrument metadata.");
        first.Add(2);
        versioned.Add(3);
        other.Add(5);
        var facade = scope.Resolve<OpenTelemetryFacade>();

        var initial = await ReadAsync(facade);
        var later = await ReadAsync(facade);

        initial.Instruments.Should().HaveCount(3);
        initial.Instruments.Select(instrument => instrument.InstrumentId).Should().OnlyHaveUniqueItems();
        initial.Instruments.Should().AllSatisfy(instrument => instrument.InstrumentId.Should().NotBeNullOrWhiteSpace());
        foreach (var instrument in initial.Instruments)
        {
            var retained = later.Instruments.Single(candidate => candidate.InstrumentId == instrument.InstrumentId);
            retained.Should().NotBeSameAs(instrument);
            retained.MeterName.Should().Be(instrument.MeterName);
            retained.MeterVersion.Should().Be(instrument.MeterVersion);
            retained.Name.Should().Be("shared");
            retained.CurrentValue.Should().Be(instrument.CurrentValue);
        }
        initial.Instruments.Single(instrument => instrument.MeterName == factory.MeterName && instrument.MeterVersion == "1.0")
            .CurrentValue.Should().Be(2);
        initial.Instruments.Single(instrument => instrument.MeterName == factory.MeterName && instrument.MeterVersion == "2.0")
            .CurrentValue.Should().Be(3);
        initial.Instruments.Single(instrument => instrument.MeterName == factory.OtherMeterName).CurrentValue.Should().Be(5);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenDirectKindsRecord_ShouldDistinguishRunningTotalsFromNativeGaugeMeasurements()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var meter = scope.Resolve<IMeterFactory>().Create(factory.MeterName);
        var counter = meter.CreateCounter<long>("counter");
        var upDown = meter.CreateUpDownCounter<long>("up-down");
        var gauge = meter.CreateGauge<double>("native-gauge", "Cel");
        counter.Add(5);
        counter.Add(2);
        upDown.Add(1);
        upDown.Add(-3);
        gauge.Record(7.25);
        gauge.Record(-2.5);

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());

        var counterSeries = snapshot.Instruments.Single(instrument => instrument.Name == "counter").TagSets.Single();
        counterSeries.Samples.Select(sample => sample.Value).Should().Equal(5, 7);
        counterSeries.CurrentValue.Should().Be(7);
        counterSeries.Delta.Should().Be(2);
        var upDownSeries = snapshot.Instruments.Single(instrument => instrument.Name == "up-down").TagSets.Single();
        upDownSeries.Samples.Select(sample => sample.Value).Should().Equal(1, -2);
        upDownSeries.CurrentValue.Should().Be(-2);
        upDownSeries.Delta.Should().Be(-3);
        var gaugeSnapshot = snapshot.Instruments.Single(instrument => instrument.Name == "native-gauge");
        gaugeSnapshot.Kind.Should().Be("Gauge");
        gaugeSnapshot.IsObservable.Should().BeFalse();
        gaugeSnapshot.Unit.Should().Be("Cel");
        var gaugeSeries = gaugeSnapshot.TagSets.Single();
        gaugeSeries.Samples.Select(sample => sample.Value).Should().Equal(7.25, -2.5);
        gaugeSeries.CurrentValue.Should().Be(-2.5);
        gaugeSeries.Delta.Should().Be(-9.75);
        gaugeSeries.LastUpdatedUtc.Should().Be(gaugeSeries.Samples[^1].TimestampUtc);
        gaugeSeries.Samples.Should().AllSatisfy(sample => sample.TimestampUtc.Offset.Should().Be(TimeSpan.Zero));
    }

    [Fact]
    public async Task Record_WhenNormalizedTagsContainSeparatorsOrChangeOrder_ShouldKeepExactDistinctSeries()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var counter = scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateCounter<long>("tag-identities");
        counter.Add(2, Tag("a", "x\u001fb\u001ey"));
        counter.Add(3, Tag("a", "x"), Tag("b", "y"));
        counter.Add(5, Tag("b", "y"), Tag("a", "x"));
        counter.Add(7);
        counter.Add(11, Tag("", ""));

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());
        var series = snapshot.Instruments.Single().TagSets;

        series.Should().HaveCount(4);
        series.Select(item => item.Key).Should().OnlyHaveUniqueItems();
        series.Single(item => item.Tags.Count == 1 && item.Tags.ContainsKey("a")).CurrentValue.Should().Be(2);
        series.Single(item => item.Tags.Count == 2).CurrentValue.Should().Be(8);
        series.Single(item => item.Tags.Count == 2).Samples.Select(sample => sample.Value).Should().Equal(3, 8);
        series.Single(item => item.Tags.Count == 0).CurrentValue.Should().Be(7);
        series.Single(item => item.Tags.ContainsKey("")).CurrentValue.Should().Be(11);
    }

    [Fact]
    public async Task Record_WhenNumericTagCultureChanges_ShouldUseOneInvariantNormalizedSeries()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var counter = scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateCounter<long>("culture-tags");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            counter.Add(2, Tag("ratio", 1.5m));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            counter.Add(3, Tag("ratio", 1.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());
        var series = snapshot.Instruments.Single().TagSets.Single();
        series.Tags["ratio"].Should().Be("1.5");
        series.CurrentValue.Should().Be(5);
        series.Samples.Select(sample => sample.Value).Should().Equal(2, 5);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenRetentionEvicts_ShouldKeepCounterTotalsAndHistogramWindowSeparate()
    {
        var factory = new OpenTelemetryTestApplicationFactory(samplesPerSeries: 2);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var meter = scope.Resolve<IMeterFactory>().Create(factory.MeterName);
        var counter = meter.CreateCounter<long>("running-count");
        var histogram = meter.CreateHistogram<double>("duration", "ms");
        for (var value = 1; value <= 4; value++)
        {
            counter.Add(1);
            histogram.Record(value);
        }

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());

        snapshot.SamplesPerSeries.Should().Be(2);
        var counterSeries = snapshot.Instruments.Single(instrument => instrument.Name == "running-count").TagSets.Single();
        counterSeries.Samples.Select(sample => sample.Value).Should().Equal(3, 4);
        counterSeries.CurrentValue.Should().Be(4);
        counterSeries.Delta.Should().Be(1);
        var histogramSeries = snapshot.Instruments.Single(instrument => instrument.Name == "duration").TagSets.Single();
        histogramSeries.Samples.Select(sample => sample.Value).Should().Equal(3, 4);
        var histogramWindow = histogramSeries.Histogram!;
        histogramWindow.Count.Should().Be(2);
        histogramWindow.Average.Should().BeApproximately(3.5, 0.000001);
        histogramWindow.Buckets.Sum(bucket => bucket.Count).Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task GetSnapshotAsync_WhenSampleCapacityIsNonpositive_ShouldReportTheActualOneSampleClamp(int capacity)
    {
        var factory = new OpenTelemetryTestApplicationFactory(samplesPerSeries: capacity);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var counter = scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateCounter<long>("clamped");
        counter.Add(2);
        counter.Add(3);

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());

        snapshot.SamplesPerSeries.Should().Be(1);
        var series = snapshot.Instruments.Single().TagSets.Single();
        series.Samples.Should().ContainSingle().Which.Value.Should().Be(5);
        series.Delta.Should().Be(0);
    }

    [Fact]
    public async Task Record_WhenNewTagSetsExceedCapacity_ShouldDropOnlyAdditionalSeriesAndContinueExistingTotals()
    {
        var factory = new OpenTelemetryTestApplicationFactory(maxTagSetsPerInstrument: 2);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var counter = scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateCounter<long>("bounded-tags");
        counter.Add(1, Tag("lane", "first"));
        counter.Add(2, Tag("lane", "second"));
        counter.Add(3, Tag("lane", "dropped"));
        counter.Add(4, Tag("lane", "dropped"));
        counter.Add(5, Tag("lane", "first"));

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());
        var instrument = snapshot.Instruments.Single();

        snapshot.MaxTagSetsPerInstrument.Should().Be(2);
        instrument.Overflow.Should().BeTrue();
        instrument.DroppedSamples.Should().Be(2);
        instrument.TagSets.Should().HaveCount(2);
        instrument.TagSets.Single(item => item.Tags["lane"] == "first").CurrentValue.Should().Be(6);
        instrument.TagSets.Single(item => item.Tags["lane"] == "second").CurrentValue.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Record_WhenTagCapacityIsNonpositive_ShouldReportZeroCapacityAndActualDroppedMeasurements(int capacity)
    {
        var factory = new OpenTelemetryTestApplicationFactory(maxTagSetsPerInstrument: capacity);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var counter = scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateCounter<long>("no-tags-retained");
        counter.Add(1);
        counter.Add(2);

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());

        snapshot.MaxTagSetsPerInstrument.Should().Be(0);
        snapshot.Instruments.Single().TagSets.Should().BeEmpty();
        snapshot.Instruments.Single().DroppedSamples.Should().Be(2);
        snapshot.HasOverflow.Should().BeTrue();
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenConsumerAttemptsMutation_ShouldProtectTagsAndDetachRetainedSamples()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var counter = scope.Resolve<IMeterFactory>().Create(factory.MeterName).CreateCounter<long>("ownership");
        counter.Add(3, Tag("lane", "original"));
        var facade = scope.Resolve<OpenTelemetryFacade>();
        var first = (await ReadAsync(facade)).Instruments.Single().TagSets.Single();
        var tagView = Assert.IsAssignableFrom<IDictionary<string, string>>(first.Tags);
        Assert.Throws<NotSupportedException>(() => tagView["lane"] = "consumer-mutated");
        Assert.IsAssignableFrom<IList<MetricPointSnapshot>>(first.Samples).Clear();
        counter.Add(2, Tag("lane", "original"));

        var retained = (await ReadAsync(facade)).Instruments.Single().TagSets.Single();

        retained.Tags.Should().NotBeSameAs(first.Tags);
        retained.Tags["lane"].Should().Be("original");
        retained.Samples.Select(sample => sample.Value).Should().Equal(3, 5);
        retained.CurrentValue.Should().Be(5);
    }

    [Fact]
    public async Task RecordObservableMeasurements_WhenAbsoluteValuesChange_ShouldUseNativeCallbacksWithoutReadResampling()
    {
        var factory = new OpenTelemetryTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var meter = scope.Resolve<IMeterFactory>().Create(factory.MeterName);
        long completed = 10;
        long active = -1;
        double temperature = 7.25;
        var counterObservations = 0;
        meter.CreateObservableCounter<long>("absolute-total", () =>
        {
            Interlocked.Increment(ref counterObservations);
            return Interlocked.Read(ref completed);
        });
        meter.CreateObservableUpDownCounter<long>("absolute-active", () => Interlocked.Read(ref active));
        meter.CreateObservableGauge<double>("absolute-gauge", () => Volatile.Read(ref temperature));
        var facade = scope.Resolve<OpenTelemetryFacade>();
        var collector = scope.Resolve<InProcessMetricsCollector>();
        var unmeasured = await ReadAsync(facade);
        unmeasured.Instruments.Should().HaveCount(3);
        unmeasured.Instruments.Should().AllSatisfy(instrument => instrument.TagSets.Should().BeEmpty());
        counterObservations.Should().Be(0);

        collector.RecordObservableMeasurements();
        Interlocked.Exchange(ref completed, 13);
        Interlocked.Exchange(ref active, -4);
        Volatile.Write(ref temperature, 2.5);
        collector.RecordObservableMeasurements();
        var snapshot = await ReadAsync(facade);
        await ReadAsync(facade);

        counterObservations.Should().Be(2);
        var total = snapshot.Instruments.Single(instrument => instrument.Name == "absolute-total");
        total.Kind.Should().Be("ObservableCounter");
        total.IsObservable.Should().BeTrue();
        total.TagSets.Single().Samples.Select(sample => sample.Value).Should().Equal(10, 13);
        total.CurrentValue.Should().Be(13);
        total.Delta.Should().Be(3);
        var activeSeries = snapshot.Instruments.Single(instrument => instrument.Name == "absolute-active").TagSets.Single();
        activeSeries.Samples.Select(sample => sample.Value).Should().Equal(-1, -4);
        activeSeries.CurrentValue.Should().Be(-4);
        var gaugeSeries = snapshot.Instruments.Single(instrument => instrument.Name == "absolute-gauge").TagSets.Single();
        gaugeSeries.Samples.Select(sample => sample.Value).Should().Equal(7.25, 2.5);
        gaugeSeries.Delta.Should().Be(-4.75);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenRawOrAggregateValuesAreNonfinite_ShouldSerializeActualDtoEvidenceWithoutGlobalOptions()
    {
        var factory = new OpenTelemetryTestApplicationFactory(samplesPerSeries: 6);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var meter = scope.Resolve<IMeterFactory>().Create(factory.MeterName);
        var gauge = meter.CreateGauge<double>("raw-nonfinite");
        gauge.Record(double.NaN);
        gauge.Record(double.PositiveInfinity);
        gauge.Record(double.NegativeInfinity);
        gauge.Record(7.5);
        var overflow = meter.CreateGauge<double>("aggregate-overflow");
        overflow.Record(double.MaxValue, Tag("part", "first"));
        overflow.Record(double.MaxValue, Tag("part", "second"));
        var histogram = meter.CreateHistogram<double>("finite-window");
        histogram.Record(double.NaN);
        histogram.Record(2);
        histogram.Record(double.PositiveInfinity);
        histogram.Record(4);
        var invalidOnly = meter.CreateHistogram<double>("invalid-only");
        invalidOnly.Record(double.NaN);
        invalidOnly.Record(double.PositiveInfinity);
        invalidOnly.Record(double.NegativeInfinity);

        var snapshot = await ReadAsync(scope.Resolve<OpenTelemetryFacade>());
        // Web naming alone does not enable named float writing; the production DTO fields own that policy.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, options));
        var instruments = json.RootElement.GetProperty("instruments").EnumerateArray().ToArray();
        var raw = instruments.Single(instrument => instrument.GetProperty("name").GetString() == "raw-nonfinite")
            .GetProperty("tagSets")[0];
        var values = raw.GetProperty("samples").EnumerateArray().Select(sample => sample.GetProperty("value")).ToArray();
        values[0].GetString().Should().Be("NaN");
        values[1].GetString().Should().Be("Infinity");
        values[2].GetString().Should().Be("-Infinity");
        values[3].ValueKind.Should().Be(JsonValueKind.Number);
        values[3].GetDouble().Should().Be(7.5);
        raw.GetProperty("delta").GetString().Should().Be("Infinity");
        instruments.Single(instrument => instrument.GetProperty("name").GetString() == "aggregate-overflow")
            .GetProperty("currentValue").GetString().Should().Be("Infinity");
        var finiteSeries = snapshot.Instruments.Single(instrument => instrument.Name == "finite-window").TagSets.Single();
        finiteSeries.Samples.Should().HaveCount(4);
        finiteSeries.FiniteSampleCount.Should().Be(2);
        finiteSeries.NonFiniteSampleCount.Should().Be(2);
        var finiteWindow = finiteSeries.Histogram!;
        finiteWindow.Count.Should().Be(2);
        finiteWindow.NonFiniteSampleCount.Should().Be(2);
        finiteWindow.Average.Should().Be(3);
        var noDistribution = snapshot.Instruments.Single(instrument => instrument.Name == "invalid-only").TagSets.Single();
        noDistribution.Histogram.Should().BeNull();
        noDistribution.FiniteSampleCount.Should().Be(0);
        noDistribution.NonFiniteSampleCount.Should().Be(3);
        var roundTrip = JsonSerializer.Deserialize<OpenTelemetrySnapshot>(json.RootElement.GetRawText(), options)!;
        var roundTripValues = roundTrip.Instruments.Single(instrument => instrument.Name == "raw-nonfinite").TagSets.Single().Samples;
        double.IsNaN(roundTripValues[0].Value).Should().BeTrue();
        roundTripValues[1].Value.Should().Be(double.PositiveInfinity);
        roundTripValues[2].Value.Should().Be(double.NegativeInfinity);
        roundTripValues[3].Value.Should().Be(7.5);
    }

    private static async Task<OpenTelemetrySnapshot> ReadAsync(OpenTelemetryFacade facade)
    {
        var result = await facade.GetSnapshotAsync();
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        return result.Data!;
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
