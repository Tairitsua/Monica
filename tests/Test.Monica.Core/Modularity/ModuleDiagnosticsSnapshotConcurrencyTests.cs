using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Facades;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

[Collection(BlockingConcurrencyCollection.Name)]
public sealed class ModuleDiagnosticsSnapshotConcurrencyTests
{
    private static readonly TimeSpan HANG_GUARD = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task GetSnapshot_WhenFirstTerminalProjectionIsConcurrent_ShouldPublishOneCachedInstance()
    {
        using var projectionGate = new SnapshotProjectionGate();
        using var host = CreateHost();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var diagnostics = host.Services.GetRequiredService<ModuleDiagnosticsService>();
        diagnostics.SetSnapshotProjectionObserver(projectionGate.WaitForRelease);
        try
        {
            var firstProjection = Task.Factory.StartNew(
                facade.GetSnapshot,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            projectionGate.WaitUntilEntered(HANG_GUARD, TestContext.Current.CancellationToken);
            var competingProjection = facade.GetSnapshot();
            projectionGate.Release();
            var firstResult = await firstProjection.WaitAsync(
                HANG_GUARD,
                TestContext.Current.CancellationToken);
            var results = new[] { firstResult, competingProjection, facade.GetSnapshot() };

            results.Should().OnlyContain(result => result.Status == ResultStatus.Ok && result.Data != null);
            var snapshots = results.Select(static result => result.Data!).ToArray();
            snapshots.Should().OnlyContain(static snapshot =>
                snapshot.IsFinal && snapshot.Outcome == ModuleCompositionOutcome.Succeeded);
            snapshots.Should().OnlyContain(snapshot => ReferenceEquals(snapshot, snapshots[0]));
            snapshots.Select(static snapshot => snapshot.Revision).Should().OnlyContain(revision =>
                revision == snapshots[0].Revision);
        }
        finally
        {
            projectionGate.Release();
            diagnostics.SetSnapshotProjectionObserver(null);
        }
    }

    [Fact]
    public async Task GetSnapshot_WhenLiveProjectionCompletesAfterTerminalPublication_ShouldReturnTerminalInstance()
    {
        using var gate = new SnapshotConcurrencyWorkGate();
        using var projectionGate = new SnapshotProjectionGate();
        using var host = CreateHost(gate);
        var application = host.Services.GetRequiredService<MonicaApplication>();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var diagnostics = host.Services.GetRequiredService<ModuleDiagnosticsService>();
        try
        {
            gate.WaitUntilEntered(HANG_GUARD, TestContext.Current.CancellationToken);
            var initial = facade.GetSnapshot().Data!;
            diagnostics.SetSnapshotProjectionObserver(projectionGate.WaitForRelease);
            var inFlightLiveProjection = Task.Factory.StartNew(
                () => facade.GetSnapshot().Data!,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            projectionGate.WaitUntilEntered(HANG_GUARD, TestContext.Current.CancellationToken);

            gate.Release();
            application.Modules.DrainStartupWork();
            var terminal = facade.GetSnapshot().Data!;
            projectionGate.Release();
            var lateProjectionResult = await inFlightLiveProjection.WaitAsync(
                HANG_GUARD,
                TestContext.Current.CancellationToken);
            var repeated = facade.GetSnapshot().Data!;

            initial.IsFinal.Should().BeFalse();
            initial.Outcome.Should().BeNull();
            initial.Summary.ActiveStartupWorkCount.Should().Be(1);
            initial.TraceSpans.Should().ContainSingle(span =>
                span.Kind == ModuleDiagnosticsTraceSpanKind.StartupWork && !span.IsComplete);

            terminal.IsFinal.Should().BeTrue();
            terminal.Outcome.Should().Be(ModuleCompositionOutcome.Succeeded);
            terminal.Revision.Should().BeGreaterThan(initial.Revision);
            terminal.Summary.ActiveStartupWorkCount.Should().Be(0);
            terminal.TraceSpans.Should().ContainSingle(span =>
                span.Kind == ModuleDiagnosticsTraceSpanKind.StartupWork && span.IsComplete);
            terminal.CompositionId.Should().Be(initial.CompositionId);
            terminal.CapturedAtUtc.Should().BeOnOrAfter(initial.CapturedAtUtc);
            lateProjectionResult.Should().BeSameAs(terminal);
            repeated.Should().BeSameAs(terminal);
        }
        finally
        {
            gate.Release();
            projectionGate.Release();
            diagnostics.SetSnapshotProjectionObserver(null);
        }
    }

    private static IHost CreateHost(SnapshotConcurrencyWorkGate? gate = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureModuleSystem(static options => options.MaxConcurrentStartupWorkItems = 2);
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModuleSystem();
            if (gate is not null)
            {
                monica.AddModule<SnapshotConcurrencyProbeModule, SnapshotConcurrencyProbeOption>(options =>
                    options.Gate = gate);
            }
        });
        return builder.Build();
    }
}

internal sealed class SnapshotProjectionGate : IDisposable
{
    private readonly ManualResetEventSlim _entered = new(initialState: false);
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private int _claimed;

    internal void WaitUntilEntered(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_entered.Wait(timeout, cancellationToken))
        {
            throw new TimeoutException("The controlled diagnostics projection did not start.");
        }
    }

    internal void WaitForRelease()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0)
        {
            return;
        }

        _entered.Set();
        _release.Wait();
    }

    internal void Release() => _release.Set();

    public void Dispose()
    {
        _entered.Dispose();
        _release.Dispose();
    }
}

internal sealed class SnapshotConcurrencyProbeModule : MonicaModule<SnapshotConcurrencyProbeOption>
{
    public override void ConfigureServices(ModuleContext<SnapshotConcurrencyProbeOption> context)
    {
        ScheduleStartupWork(
            "snapshot-concurrency-probe",
            Option.Gate.Run,
            ModuleStartupWorkBarrier.NoBarrier);
    }
}

internal sealed class SnapshotConcurrencyProbeOption : ModuleOptions<SnapshotConcurrencyProbeModule>
{
    internal SnapshotConcurrencyWorkGate Gate { get; set; } = null!;
}

internal sealed class SnapshotConcurrencyWorkGate : IDisposable
{
    private readonly ManualResetEventSlim _entered = new(initialState: false);
    private readonly ManualResetEventSlim _release = new(initialState: false);

    internal void WaitUntilEntered(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_entered.Wait(timeout, cancellationToken))
        {
            throw new TimeoutException("The controlled snapshot-concurrency work did not start.");
        }
    }

    internal void Run()
    {
        _entered.Set();
        _release.Wait();
    }

    internal void Release() => _release.Set();

    public void Dispose()
    {
        _entered.Dispose();
        _release.Dispose();
    }
}
