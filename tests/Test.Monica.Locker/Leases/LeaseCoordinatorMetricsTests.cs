using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Locker.Leases;
using Monica.Locker.Leases.Metrics;
using Monica.Testing.Time;
using NSubstitute;
using Xunit;

namespace Test.Monica.Locker.Leases;

public sealed class LeaseCoordinatorMetricsTests
{
    private static readonly DateTimeOffset START = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly LeaseResource RESOURCE = new("private-deployment", "private-resource");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Operations_WhenTwoCoordinatorsShareAFactory_ShouldShareInstrumentsAndExcludeCachedReads()
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        await using var holder = Create(store, time, probe, "private-holder");
        await using var standby = Create(store, time, probe, "private-standby");

        var term = Assert.IsType<LeaseContext>(await holder.GetAsync(RESOURCE, Token));
        Assert.Same(term, await holder.GetAsync(RESOURCE, Token));
        Assert.Null(await standby.GetAsync(RESOURCE, Token));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(20), Token);
        await holder.DisposeAsync();
        await standby.DisposeAsync();

        Assert.Equal(3, probe.Instruments.Count);
        var operations = probe.Measurements(LeaseMetricNames.Operations);
        Assert.Equal(4, operations.Length);
        AssertOperation(operations, "acquire", "success");
        AssertOperation(operations, "acquire", "contended");
        AssertOperation(operations, "renew", "success");
        AssertOperation(operations, "release", "success");
        Assert.All(operations, measurement => Assert.Equal(new[] { "operation", "result" }, measurement.Tags.Keys.Order().ToArray()));
        var durations = probe.Measurements(LeaseMetricNames.OperationDuration);
        Assert.Equal(4, durations.Length);
        Assert.All(durations, measurement => Assert.True(measurement.Value >= 0));
        Assert.Equal("stopped", Assert.Single(probe.Measurements(LeaseMetricNames.TermsEnded)).Tags["reason"]);
    }

    [Fact]
    public async Task Metrics_WhenCoordinatorsAreConstructedConcurrently_ShouldPublishOneInstrumentSet()
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        const int contenders = 8;
        using var start = new Barrier(contenders);
        var cancellationToken = Token;
        var coordinators = await Task.WhenAll(Enumerable.Range(0, contenders).Select(index =>
            Task.Factory.StartNew(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken))
                    throw new TimeoutException("Concurrent coordinator construction did not reach the start barrier.");
                return Create(store, time, probe, $"holder-{index}");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)))
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        try
        {
            Assert.Equal(3, probe.Instruments.Count);
            for (var index = 0; index < contenders; index++)
                Assert.NotNull(await coordinators[index].GetAsync(new LeaseResource("concurrent", $"role-{index}"), cancellationToken));
            Assert.Equal(contenders, probe.Measurements(LeaseMetricNames.Operations).Length);
        }
        finally
        {
            foreach (var coordinator in coordinators) await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Operations_WhenHostsUseDifferentFactories_ShouldRemainIsolated()
    {
        using var firstHost = new MetricsProbe();
        using var secondHost = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        await using var first = Create(store, time, firstHost, "first");
        await using var second = Create(store, time, secondHost, "second");
        Assert.NotNull(await first.GetAsync(RESOURCE, Token));
        Assert.Null(await second.GetAsync(RESOURCE, Token));

        AssertOperation(firstHost.Measurements(LeaseMetricNames.Operations), "acquire", "success");
        AssertOperation(secondHost.Measurements(LeaseMetricNames.Operations), "acquire", "contended");
        Assert.Single(firstHost.Measurements(LeaseMetricNames.Operations));
        Assert.Single(secondHost.Measurements(LeaseMetricNames.Operations));
        Assert.All(firstHost.Instruments.Keys, instrument => Assert.Same(firstHost.Factory, instrument.Meter.Scope));
        Assert.All(secondHost.Instruments.Keys, instrument => Assert.Same(secondHost.Factory, instrument.Meter.Scope));
    }

    [Fact]
    public async Task Renewal_WhenRejected_ShouldRecordLossOnceWithoutReclassifyingDisposalAsLoss()
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        boundary.TryRenewAsync(Arg.Any<LeaseIdentity>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LeaseGrant?>(null));
        await using var coordinator = Create(boundary, time, probe);
        var term = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = term.StoppingToken.Register(() => lost.TrySetResult());
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await lost.Task.WaitAsync(Token);
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        AssertOperation(probe.Measurements(LeaseMetricNames.Operations), "renew", "lost");
        Assert.Equal("renewal_rejected", Assert.Single(probe.Measurements(LeaseMetricNames.TermsEnded)).Tags["reason"]);
    }

    [Fact]
    public async Task Renewal_WhenItsDeadlineExpires_ShouldCountTimeoutAndNotReviveMetricsForTheLateReply()
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var durable = new InMemoryLeaseStore(time);
        var boundary = ForwardTo(durable);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<LeaseGrant?>(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.TryRenewAsync(Arg.Any<LeaseIdentity>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { entered.TrySetResult(); return reply.Task; });
        await using var coordinator = Create(boundary, time, probe);
        var term = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = term.StoppingToken.Register(() => lost.TrySetResult());
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(Token);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(15), Token);
        time.Advance(TimeSpan.FromSeconds(5));
        await lost.Task.WaitAsync(Token);
        reply.SetResult(await durable.TryRenewAsync(term.Identity, TimeSpan.FromSeconds(30), Token));
        await coordinator.DisposeAsync();

        Assert.True(term.StoppingToken.IsCancellationRequested);
        AssertOperation(probe.Measurements(LeaseMetricNames.Operations), "renew", "timeout");
        Assert.DoesNotContain(probe.Measurements(LeaseMetricNames.Operations), measurement =>
            measurement.Tags["operation"] == "renew" && measurement.Tags["result"] == "success");
        Assert.Equal("renewal_timeout", Assert.Single(probe.Measurements(LeaseMetricNames.TermsEnded)).Tags["reason"]);
        var duration = Assert.Single(probe.Measurements(LeaseMetricNames.OperationDuration), measurement =>
            measurement.Tags["operation"] == "renew");
        Assert.Equal(5, duration.Value);
    }

    [Fact]
    public async Task Term_WhenTheProcessPausesPastExpiry_ShouldRecordExpiryOnce()
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        await using var coordinator = Create(new InMemoryLeaseStore(time), time, probe);
        var term = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.AdvanceAfterPause(TimeSpan.FromSeconds(31));
        await coordinator.DisposeAsync();

        Assert.True(term.StoppingToken.IsCancellationRequested);
        Assert.Equal("expired", Assert.Single(probe.Measurements(LeaseMetricNames.TermsEnded)).Tags["reason"]);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("timeout")]
    public async Task Acquisition_WhenTheStoreThrows_ShouldPreserveTheExceptionAndReportItsOutcome(string result)
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var boundary = Substitute.For<IExclusiveLeaseStore>();
        Exception failure = result switch
        {
            "cancelled" => new OperationCanceledException(),
            "timeout" => new TimeoutException(),
            _ => new IOException("The store response is unavailable.")
        };
        boundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LeaseGrant?>(failure));
        await using var coordinator = Create(boundary, time, probe);

        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => coordinator.GetAsync(RESOURCE, Token)));
        AssertOperation(probe.Measurements(LeaseMetricNames.Operations), "acquire", result);
        Assert.Empty(probe.Measurements(LeaseMetricNames.TermsEnded));
    }

    [Fact]
    public async Task Release_WhenItsOwnDeadlineCancelsTheStore_ShouldReportTimeout()
    {
        using var probe = new MetricsProbe();
        var time = new ControllableTimeProvider(START);
        var boundary = ForwardTo(new InMemoryLeaseStore(time));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
                return true;
            });
        using var coordinator = new LeaseCoordinator(boundary, "holder", time, NullLogger.Instance,
            new LeaseCoordinatorOptions { OperationTimeout = TimeSpan.FromMilliseconds(250) }, probe.Factory);
        Assert.NotNull(await coordinator.GetAsync(RESOURCE, Token));

        var disposing = coordinator.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var failure = await Record.ExceptionAsync(() => disposing.WaitAsync(TimeSpan.FromSeconds(10), Token));

        // The provider's cancellation and WaitAsync deadline can race; both are local timeout outcomes.
        Assert.True(failure is OperationCanceledException or TimeoutException);
        AssertOperation(probe.Measurements(LeaseMetricNames.Operations), "release", "timeout");
        Assert.Equal("stopped", Assert.Single(probe.Measurements(LeaseMetricNames.TermsEnded)).Tags["reason"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metrics_WhenAnObserverThrows_ShouldStillAcquireRenewAndRelease(bool failPublication)
    {
        using var probe = new MetricsProbe(failPublication, failMeasurements: !failPublication);
        var time = new ControllableTimeProvider(START);
        var store = new InMemoryLeaseStore(time);
        await using var coordinator = Create(store, time, probe);
        var term = Assert.IsType<LeaseContext>(await coordinator.GetAsync(RESOURCE, Token));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), Token);
        time.Advance(TimeSpan.FromSeconds(10));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(20), Token);

        Assert.False(term.StoppingToken.IsCancellationRequested);
        Assert.Equal(START.AddSeconds(40), Assert.Single((await store.GetSnapshotAsync(RESOURCE.Scope, RESOURCE.Name, Token)).Leases).ExpiresAtUtc);
        await coordinator.DisposeAsync();
        Assert.Empty((await store.GetSnapshotAsync(RESOURCE.Scope, RESOURCE.Name, Token)).Leases);
    }

    private static LeaseCoordinator Create(IExclusiveLeaseStore store, TimeProvider time, MetricsProbe probe,
        string holder = "holder") => new(store, holder, time, NullLogger.Instance, meterFactory: probe.Factory);

    private static void AssertOperation(IEnumerable<Measurement> measurements, string operation, string result)
    {
        var measurement = Assert.Single(measurements, item => item.Tags["operation"] == operation && item.Tags["result"] == result);
        Assert.Equal(1, measurement.Value);
    }

    private static IExclusiveLeaseStore ForwardTo(InMemoryLeaseStore store)
    {
        var boundary = Substitute.For<IExclusiveLeaseStore>();
        boundary.TryAcquireAsync(Arg.Any<LeaseResource>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => store.TryAcquireAsync(call.Arg<LeaseResource>(), call.Arg<string>(), call.Arg<TimeSpan>(), call.Arg<CancellationToken>()));
        boundary.TryReleaseAsync(Arg.Any<LeaseIdentity>(), Arg.Any<CancellationToken>())
            .Returns(call => store.TryReleaseAsync(call.Arg<LeaseIdentity>(), call.Arg<CancellationToken>()));
        return boundary;
    }

    private sealed record Measurement(string Name, double Value, Dictionary<string, string> Tags);

    private sealed class MetricsProbe : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();
        internal IMeterFactory Factory { get; }
        internal ConcurrentDictionary<Instrument, byte> Instruments { get; } = new();

        internal MetricsProbe(bool failPublication = false, bool failMeasurements = false)
        {
            _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
            Factory = _services.GetRequiredService<IMeterFactory>();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (!ReferenceEquals(instrument.Meter.Scope, Factory) || instrument.Meter.Name != LeaseMetricNames.MeterName) return;
                if (failPublication) throw new InvalidOperationException("Instrument publication observer failed.");
                Instruments.TryAdd(instrument, 0);
                listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Capture(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Capture(instrument, value, tags));
            _listener.Start();

            void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                if (failMeasurements) throw new InvalidOperationException("Measurement observer failed.");
                var copy = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var tag in tags) copy.Add(tag.Key, Assert.IsType<string>(tag.Value));
                _measurements.Enqueue(new Measurement(instrument.Name, value, copy));
            }
        }

        internal Measurement[] Measurements(string name) => _measurements.Where(measurement => measurement.Name == name).ToArray();

        public void Dispose()
        {
            _listener.Dispose();
            _services.Dispose();
        }
    }
}
