using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.Profiling.MemoryDiagnostics.Facades;
using Monica.Profiling.MemoryDiagnostics.Services;
using Monica.Profiling.UIMemoryAnalysis.State;
using Test.Monica.Profiling.UIMemoryAnalysis.Support;
using Xunit;

namespace Test.Monica.Profiling.MemoryDiagnostics.Facades;

public sealed class MemoryDiagnosticsFacadeTests
{
    [Fact]
    public async Task TriggerGcDumpAsync_WhenCreatorReturnsAPath_ShouldKeepItAsDataAndPageActionEvidence()
    {
        const string explicitPath = "fixture-host/requested path <literal>.gcdump";
        const string defaultPath = "fixture-host/default capture.gcdump";
        var requestedPaths = new List<string?>();
        var failCreation = false;
        // The module/service composition remains real; this creator returns structural path evidence
        // at the external diagnostic boundary and never creates a file or process.
        await using var application = await new MemoryAnalysisTestApplicationFactory(
                refreshIntervalMs: 0, allowGcDump: true, sampleIntervalMs: int.MaxValue)
            .CreateAsync(scenario => scenario.With<MemoryDiagnosticsFacade>(provider => new(
                provider.GetRequiredService<MemoryDiagnosticsService>(),
                provider.GetRequiredService<ILogger<MemoryDiagnosticsFacade>>(),
                path =>
                {
                    requestedPaths.Add(path);
                    return failCreation
                        ? Task.FromException<string>(new IOException("Controlled dump creation failed."))
                        : Task.FromResult(path ?? defaultPath);
                })), TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<MemoryDiagnosticsFacade>();

        var direct = await facade.TriggerGcDumpAsync(explicitPath);

        direct.Status.Should().Be(ResultStatus.Ok);
        direct.Data.Should().Be(explicitPath);
        direct.Message.Should().BeNullOrEmpty();
        await using var state = scope.Resolve<MemoryAnalysisPageStateFactory>().Create(autoRefresh: false);
        var pageResult = await state.CreateGcDumpAsync();
        pageResult.Status.Should().Be(ResultStatus.Ok);
        pageResult.Data.Should().Be(defaultPath);
        var action = state.LastAction!;
        action.IsSuccessful.Should().BeTrue();
        action.Action.Should().Be(MemoryDiagnosticAction.GcDump);
        action.ServerDumpPath.Should().Be(defaultPath);
        action.Message.Should().BeNullOrEmpty();
        requestedPaths.Should().Equal(explicitPath, null);

        failCreation = true;
        var failure = await facade.TriggerGcDumpAsync(explicitPath);
        failure.Status.Should().Be(ResultStatus.BadRequest);
        failure.Data.Should().BeNull();
        failure.Message.Should().Contain("Controlled dump creation failed.");
    }
}
