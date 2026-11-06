using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.ObservableInstance.Abstractions.Internal;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.ObservableInstance.Facades;
using Monica.Core.ObservableInstance.Models;
using Monica.Core.ObservableInstance.Services;
using Monica.Core.Results;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.ObservableInstance;

public sealed class ObservableSnapshotTests
{
    [Fact]
    public void Capture_PreservesFullDetachedValuesAndExceptionEvidence()
    {
        var registry = CreateRegistry();
        var tracker = registry.Register("one");
        var payload = new Payload { Content = new string('x', 1400) + "完整证据" };
        var exception = new InvalidOperationException("retained", new IOException("inner"));
        tracker.RecordState("record", payload, exception);
        var capture = tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
        payload.Content = "changed";
        tracker.RecordState("new state", "replacement");
        tracker.ClearHistory();

        capture.CurrentState.Text.Should().Contain(new string('x', 1400));
        capture.CurrentState.Text.Should().NotContain("changed");
        capture.History.Should().HaveCount(1);
        capture.History[0].Exception!.Details.Should().Contain("inner");
        capture.History[0].CurrentState.Should().Be(capture.CurrentState);
        capture.Identity.RegistrationId.Should().Be(tracker.RegistrationId);
    }

    [Fact]
    public void Capture_ReportsSerializationFailureWithoutInventingValue()
    {
        var tracker = CreateRegistry().Register("invalid");
        tracker.RecordState("invalid value", new FailingPayload());
        var snapshot = tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
        snapshot.CurrentState.Text.Should().BeNull();
        snapshot.CurrentState.TypeName.Should().Contain(nameof(FailingPayload));
        snapshot.CurrentState.CaptureError.Should().NotBeNullOrEmpty();
        snapshot.History[0].CurrentState.CaptureError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Clear_PreservesCurrentValueLifetimeTotalsAndMonotonicSequence()
    {
        var tracker = CreateRegistry().Register("one", o => o.MaxHistorySize = 2);
        tracker.RecordState("first", "ready", new Exception("error"));
        tracker.RecordState("message only");
        tracker.RecordState("third", "recovered");
        var before = tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
        before.History.Select(e => e.Sequence).Should().Equal(2, 3);
        tracker.ClearHistory();
        var cleared = tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
        cleared.History.Should().BeEmpty();
        cleared.CurrentState.Should().Be(before.CurrentState);
        cleared.LastRecordedAt.Should().Be(before.LastRecordedAt);
        cleared.TotalRecords.Should().Be(3);
        cleared.TotalExceptions.Should().Be(1);
        tracker.RecordState("fourth");
        tracker.CaptureSnapshot(TestContext.Current.CancellationToken).History.Single().Sequence.Should().Be(4);
    }

    [Fact]
    public async Task Clear_RejectsReplacementRegistrationAndCanceledCommand()
    {
        var registry = CreateRegistry();
        var facade = CreateFacade(registry);
        var first = registry.Register("one");
        first.RecordState("old", "ready");
        var identity = first.CaptureSnapshot(TestContext.Current.CancellationToken).Identity;
        ((IObservableInstanceRegistryWriter)registry).Unregister("one").Should().BeTrue();
        var replacement = registry.Register("one");
        replacement.RecordState("replacement", "ready");
        var rejected = await facade.ClearInstanceHistoryAsync(identity, TestContext.Current.CancellationToken);
        rejected.IsFailure().Should().BeTrue();
        replacement.Count.Should().Be(1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var action = () => facade.ClearInstanceHistoryAsync(replacement.CaptureSnapshot(TestContext.Current.CancellationToken).Identity, canceled.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        replacement.Count.Should().Be(1);
    }

    [Fact]
    public void Capture_CurrentMappingIsIndependentOfRecordedExceptionLevel()
    {
        var tracker = CreateRegistry().Register("one").SetInformationStates(State.Ready).SetErrorStates(State.Failed);
        tracker.RecordState("failed", State.Failed, new Exception("retained failure"));
        tracker.RecordState("recovered", State.Ready);
        tracker.RecordState("message with explicit error level", exception: new Exception("message"), logLevel: LogLevel.Error);
        var captured = tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
        captured.CurrentLogLevel.Should().Be(LogLevel.Information);
        captured.History[^1].LogLevel.Should().Be(LogLevel.Error);
        captured.TotalRecords.Should().Be(3);
        captured.TotalExceptions.Should().Be(2);
    }

    [Fact]
    public async Task Capture_ConcurrentWritesKeepCountersRetentionAndSequencesCoherent()
    {
        var tracker = CreateRegistry().Register("one", o => o.MaxHistorySize = 30);
        using var started = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            started.Set();
            for (var i = 1; i <= 4000; i++) tracker.RecordState("record", i, i % 4 == 0 ? new Exception("evidence") : null);
        }, TestContext.Current.CancellationToken);
        started.Wait(TestContext.Current.CancellationToken);
        for (var i = 0; i < 80; i++)
        {
            var snapshot = tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
            snapshot.History.Count.Should().Be(Math.Min(30, snapshot.TotalRecords));
            if (snapshot.History.Count > 0)
            {
                snapshot.History[^1].Sequence.Should().Be(snapshot.TotalRecords);
                snapshot.CurrentState.Text.Should().Be(snapshot.TotalRecords.ToString());
                snapshot.TotalExceptions.Should().Be(snapshot.TotalRecords / 4);
            }
        }
        await writer;
        tracker.CaptureSnapshot(TestContext.Current.CancellationToken).TotalRecords.Should().Be(4000);
    }

    [Fact]
    public async Task Capture_UsesOneHostRegistryAndLeavesExistingCapturesStableAfterRemoval()
    {
        var first = CreateRegistry();
        var second = CreateRegistry();
        first.Register("same").RecordState("host one", "first");
        second.Register("same").RecordState("host two", "second");
        var result = await CreateFacade(first).CaptureAsync(TestContext.Current.CancellationToken);
        result.IsSuccess().Should().BeTrue();
        var snapshots = result.Data;
        snapshots!.Single().CurrentState.Text.Should().Be("first");
        ((IObservableInstanceRegistryWriter)first).Unregister("same");
        snapshots!.Single().CurrentState.Text.Should().Be("first");
        var empty = await CreateFacade(first).CaptureAsync(TestContext.Current.CancellationToken);
        empty.IsSuccess().Should().BeTrue();
        empty.Data.Should().BeEmpty();
        second.GetById("same")!.CaptureSnapshot(TestContext.Current.CancellationToken).CurrentState.Text.Should().Be("second");
    }

    [Fact]
    public void Capture_CancellationAndDisposalDoNotReturnPartialEvidence()
    {
        var tracker = CreateRegistry().Register("one");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var capture = () => tracker.CaptureSnapshot(canceled.Token);
        capture.Should().Throw<OperationCanceledException>();
        tracker.Dispose();
        tracker.Dispose();
        var disposed = () => tracker.CaptureSnapshot(TestContext.Current.CancellationToken);
        disposed.Should().Throw<ObjectDisposedException>();
        var mapping = () => tracker.SetDefaultLogLevel(LogLevel.Warning);
        mapping.Should().Throw<ObjectDisposedException>();
    }

    private static ObservableInstanceRegistry CreateRegistry() => new(Options.Create(new ModuleObservableInstanceOption()));

    [Fact]
    public async Task ComposedHosts_OwnIndependentRegistryOptionsAndCaptureFacades()
    {
        static IHost CreateHost(int capacity)
        {
            var builder = Host.CreateApplicationBuilder();
            builder.AddMonica(monica =>
            {
                monica.ConfigureTypeDiscovery(d => d.ExcludeDefault());
                monica.AddObservableInstance(o => o.DefaultMaxHistorySize = capacity);
            });
            return builder.Build();
        }
        using var first = CreateHost(2);
        using var second = CreateHost(5);
        var one = first.Services.GetRequiredService<IObservableInstanceRegistry>().Register("same");
        var two = second.Services.GetRequiredService<IObservableInstanceRegistry>().Register("same");
        for (var i = 0; i < 4; i++) { one.RecordState("first host", i); two.RecordState("second host", i); }
        var firstRead = await first.Services.GetRequiredService<ObservableInstanceFacade>().CaptureAsync(TestContext.Current.CancellationToken);
        var secondRead = await second.Services.GetRequiredService<ObservableInstanceFacade>().CaptureAsync(TestContext.Current.CancellationToken);
        firstRead.IsSuccess().Should().BeTrue();
        secondRead.IsSuccess().Should().BeTrue();
        var firstInstances = firstRead.Data;
        var secondInstances = secondRead.Data;
        firstInstances!.Single().History.Should().HaveCount(2);
        secondInstances!.Single().History.Should().HaveCount(4);
        firstInstances!.Single().Identity.Should().NotBe(secondInstances!.Single().Identity);
    }
    private static ObservableInstanceFacade CreateFacade(ObservableInstanceRegistry registry) => new(registry, NullLogger<ObservableInstanceFacade>.Instance);
    private enum State { Ready, Failed }
    private sealed class Payload { public string Content { get; set; } = string.Empty; }
    private sealed class FailingPayload { public string Content => throw new InvalidOperationException("Value cannot be serialized."); }
}
