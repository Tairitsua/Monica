using AwesomeAssertions;
using Monica.Core.Results;
using Monica.UI.UISystemInfo.Models;
using Monica.UI.UISystemInfo.State;
using Xunit;

namespace Test.Monica.UI.UISystemInfo.State;

public sealed class SystemInfoPageSessionTests
{
    [Fact]
    public async Task RefreshAsync_WhenCaptureFails_ShouldPreservePreviousSnapshot()
    {
        var first = SystemInfoTestData.Snapshot();
        var callCount = 0;
        await using var session = new SystemInfoPageSession(
            SystemInfoTestData.Calls(() => ++callCount == 1
                ? Result.Success(first)
                : Result.Failure<SystemInfoSnapshot>("refresh failed")),
            clockInterval: TimeSpan.FromHours(1));

        await session.InitializeAsync();
        var refreshed = await session.RefreshAsync();

        refreshed.Should().BeFalse();
        session.Snapshot.Should().BeSameAs(first);
        session.LoadState.Should().Be(SystemInfoPageLoadState.Ready);
        session.LoadError.Should().BeNull();
        session.RefreshError.Should().Be("refresh failed");
        session.IsRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_WhenAnotherRequestOwnsTheBoundary_ShouldRejectDuplicateRefresh()
    {
        var snapshot = SystemInfoTestData.Snapshot();
        var facadeCallCount = 0;
        await using var session = new SystemInfoPageSession(
            SystemInfoTestData.Calls(() =>
            {
                facadeCallCount++;
                return Result.Success(snapshot);
            }),
            clockInterval: TimeSpan.FromHours(1));
        await session.InitializeAsync();
        var notificationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += WaitForReleaseAsync;

        var firstRefresh = session.RefreshAsync();
        await notificationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        (await session.RefreshAsync()).Should().BeFalse();
        releaseNotification.SetResult(true);
        (await firstRefresh).Should().BeTrue();
        facadeCallCount.Should().Be(2);

        Task WaitForReleaseAsync()
        {
            notificationEntered.TrySetResult(true);
            return releaseNotification.Task;
        }
    }

    [Fact]
    public async Task Changed_WhenRendererNotificationFails_ShouldDetachItAndKeepSessionUsable()
    {
        var snapshot = SystemInfoTestData.Snapshot();
        await using var session = new SystemInfoPageSession(
            SystemInfoTestData.Calls(() => Result.Success(snapshot)),
            clockInterval: TimeSpan.FromHours(1));
        var notificationCount = 0;
        session.Changed += () =>
        {
            notificationCount++;
            throw new ObjectDisposedException("renderer");
        };

        await session.InitializeAsync();
        var refreshed = await session.RefreshAsync();

        refreshed.Should().BeTrue();
        notificationCount.Should().Be(1);
        session.Snapshot.Should().BeSameAs(snapshot);
    }
}
