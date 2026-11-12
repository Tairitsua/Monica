using Monica.Core.Results;
using Monica.DevOps.Terminal.Models;
using Test.Monica.DevOps.Terminal.Support;
using Xunit;

namespace Test.Monica.DevOps.Terminal.Facades;

public sealed class TerminalFacadeTests
{
    [Fact]
    public async Task StartCommandAsync_WhenAccepted_ShouldReturnIdentityWhileLaterFailureRemainsSessionEvidence()
    {
        await using var fixture = new TerminalRuntimeFixture();
        var session = fixture.CreateSession();
        var process = fixture.Factory.NextProcess;
        process.DrainFailure = new InvalidOperationException("late producer failure");
        var response = await fixture.Facade.StartCommandAsync("owner", session.Id, new() { Command = "harmless" });
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Null(response.Message);
        var acceptance = Assert.IsType<TerminalCommandAcceptance>(response.Data);
        Assert.True(acceptance.Session.IsRunning);
        Assert.Equal(acceptance.CommandId, acceptance.Session.ActiveCommand!.CommandId);
        process.Complete();
        var completed = await fixture.WaitForIdleAsync("owner", session.Id, TestContext.Current.CancellationToken);
        Assert.Null(completed.LastResult);
        Assert.Equal(acceptance.CommandId, completed.LastExecutionFailure!.CommandId);
        Assert.Contains("late producer failure", completed.LastExecutionFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelCommandAsync_WhenRequested_ShouldReturnRunningSnapshotAndRejectStaleIdentity()
    {
        await using var fixture = new TerminalRuntimeFixture();
        var session = fixture.CreateSession();
        var process = fixture.Factory.NextProcess;
        var accepted = await fixture.Facade.StartCommandAsync("owner", session.Id, new() { Command = "harmless" });
        var request = await fixture.Facade.CancelCommandAsync("owner", session.Id, accepted.Data!.CommandId);
        Assert.Equal(ResultStatus.Ok, request.Status);
        Assert.Null(request.Message);
        var snapshot = Assert.IsType<TerminalSessionSnapshot>(request.Data);
        Assert.True(snapshot.IsRunning);
        Assert.True(snapshot.ActiveCommand!.CancellationRequested);
        var stale = await fixture.Facade.CancelCommandAsync("owner", session.Id, "stale-command");
        Assert.Equal(ResultStatus.BadRequest, stale.Status);
        Assert.Contains("Messages:CancelFailed", stale.Message, StringComparison.Ordinal);
        Assert.Null(stale.Data);
        await process.KillRequested.WaitAsync(TestContext.Current.CancellationToken);
        process.Complete();
        var result = (await fixture.WaitForIdleAsync("owner", session.Id, TestContext.Current.CancellationToken)).LastResult!;
        Assert.True(result.Canceled);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenProcessExitsNonzero_ShouldReturnSuccessfulEnvelopeWithCompletedData()
    {
        await using var fixture = new TerminalRuntimeFixture();
        var session = fixture.CreateSession();
        var process = fixture.Factory.NextProcess;
        var execution = fixture.Facade.ExecuteCommandAsync("owner", session.Id, new() { Command = "harmless" }, ct: TestContext.Current.CancellationToken);
        process.Emit("diagnostic", TerminalOutputStreams.Stderr);
        process.Complete(9);
        var response = await execution;
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Null(response.Message);
        var result = Assert.IsType<TerminalCommandResult>(response.Data);
        Assert.Equal(9, result.ExitCode);
        Assert.False(result.Canceled);
        Assert.Single(result.Output);
        Assert.False(fixture.SessionService.GetSession("owner", session.Id).IsRunning);
    }

    [Fact]
    public async Task StartCommandAsync_WhenBusyMissingOrInvalid_ShouldProjectRealAdmissionFailures()
    {
        await using var fixture = new TerminalRuntimeFixture();
        var session = fixture.CreateSession();
        var invalid = await fixture.Facade.StartCommandAsync("owner", session.Id, new() { Command = " " });
        var missing = await fixture.Facade.StartCommandAsync("owner", "missing-session", new() { Command = "harmless" });
        fixture.SessionService.StartCommand("owner", session.Id, new() { Command = "running" });
        var busy = await fixture.Facade.StartCommandAsync("owner", session.Id, new() { Command = "another" });
        foreach (var response in new[] { invalid, missing, busy })
        {
            Assert.Equal(ResultStatus.BadRequest, response.Status);
            Assert.Contains("Messages:RunCommandFailed", response.Message, StringComparison.Ordinal);
            Assert.Null(response.Data);
        }
        Assert.Single(fixture.Factory.Processes);
        fixture.Factory.Processes[0].Complete();
    }

    [Fact]
    public async Task GetEnvironmentAsync_WhenCallerRetainsModifiedCopy_ShouldLeaveActualDetectionUnchanged()
    {
        await using var fixture = new TerminalRuntimeFixture();
        var response = await fixture.Facade.GetEnvironmentAsync();
        Assert.Equal(ResultStatus.Ok, response.Status);
        Assert.Null(response.Message);
        var evidence = Assert.IsType<TerminalEnvironmentSnapshot>(response.Data);
        var modified = evidence with { ShellPath = "caller-value", ShellArguments = [] };
        var reread = (await fixture.Facade.GetEnvironmentAsync()).Data!;
        Assert.NotEqual(modified.ShellPath, reread.ShellPath);
        Assert.Equal(evidence.ShellArguments.ToArray(), reread.ShellArguments.ToArray());
        Assert.Equal(fixture.Detector.Detect().ShellPath, reread.ShellPath);
    }
}
