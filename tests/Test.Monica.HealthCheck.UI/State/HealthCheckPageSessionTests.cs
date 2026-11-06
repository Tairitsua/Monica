using AwesomeAssertions;
using Monica.Core.Results;
using Monica.HealthCheck.Models;
using Monica.HealthCheck.UI.State;
using Xunit;

namespace Test.Monica.HealthCheck.UI.State;

public sealed class HealthCheckPageSessionTests
{
    [Fact]
    public async Task InitializeAsync_ShouldExposeReadinessCountsAndComposableFilters()
    {
        var facadeCalls = 0;
        await using var session = CreateSession((_, _) =>
        {
            facadeCalls++;
            return Task.FromResult(Result.Success(CreateSnapshot()));
        });

        await session.InitializeAsync();
        await session.SetStatusFilterAsync(HealthCheckState.Unhealthy);
        await session.SetTagFilterAsync("ready");
        await session.SetNameFilterAsync("database");

        facadeCalls.Should().Be(1);
        session.LoadState.Should().Be(HealthCheckPageLoadState.Ready);
        session.ReadinessStatus.Should().Be(HealthCheckState.Unhealthy);
        session.ReadinessEntryCount.Should().Be(2);
        session.Snapshot!.HealthyCount.Should().Be(1);
        session.Snapshot!.UnhealthyCount.Should().Be(1);
        session.FilteredEntries.Should().ContainSingle()
            .Which.Name.Should().Be("database");
        session.AvailableTags.Should().Contain("live");
        session.AvailableTags.Should().Contain("ready");
    }

    [Fact]
    public async Task RefreshAsync_WhenRequestIsActive_ShouldJoinTrackedRequestWithoutOverlap()
    {
        var facadeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFacade = new TaskCompletionSource<Result<HealthCheckSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeCalls = 0;
        await using var session = CreateSession(async (_, _) =>
        {
            facadeCalls++;
            facadeEntered.SetResult();
            return await releaseFacade.Task;
        });

        var first = session.RefreshAsync();
        await facadeEntered.Task;
        var second = session.RefreshAsync();

        second.Should().BeSameAs(first);
        facadeCalls.Should().Be(1);

        releaseFacade.SetResult(Result.Success(CreateSnapshot()));
        (await first).Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_WhenAccessIsDenied_ShouldNeverInvokeFacade()
    {
        var facadeCalls = 0;
        await using var session = CreateSession(
            (_, _) =>
            {
                facadeCalls++;
                return Task.FromResult(Result.Success(CreateSnapshot()));
            },
            authorize: _ => Task.FromResult(false));

        await session.InitializeAsync();

        session.IsAccessDenied.Should().BeTrue();
        session.Snapshot.Should().BeNull();
        facadeCalls.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAsync_WhenAuthorizationIsRevoked_ShouldPreserveSnapshotAndSkipFacade()
    {
        var authorized = true;
        var facadeCalls = 0;
        await using var session = CreateSession(
            (_, _) =>
            {
                facadeCalls++;
                return Task.FromResult(Result.Success(CreateSnapshot()));
            },
            authorize: _ => Task.FromResult(authorized));

        await session.InitializeAsync();
        var snapshot = session.Snapshot;
        authorized = false;

        (await session.RefreshAsync()).Should().BeFalse();

        session.IsAccessDenied.Should().BeTrue();
        session.Snapshot.Should().BeSameAs(snapshot);
        facadeCalls.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_ShouldCancelAndAwaitTheTrackedRefresh()
    {
        var facadeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var facadeExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(async (_, cancellationToken) =>
        {
            facadeEntered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Result.Success(CreateSnapshot());
            }
            finally
            {
                facadeExited.SetResult();
            }
        });
        var refresh = session.RefreshAsync();
        await facadeEntered.Task;

        await session.DisposeAsync();

        await facadeExited.Task;
        (await refresh).Should().BeFalse();
        Func<Task> disposeAgain = () => session.DisposeAsync().AsTask();
        await disposeAgain.Should().NotThrowAsync();
    }

    private static HealthCheckPageSession CreateSession(
        Func<HealthCheckScope, CancellationToken, Task<Result<HealthCheckSnapshot>>> getSnapshot,
        Func<CancellationToken, Task<bool>>? authorize = null)
    {
        return new HealthCheckPageSession(
            getSnapshot,
            authorize ?? (_ => Task.FromResult(true)),
            "test-host",
            refreshInterval: TimeSpan.FromHours(1));
    }

    private static HealthCheckSnapshot CreateSnapshot() => new()
    {
        Scope = HealthCheckScope.All,
        Status = HealthCheckState.Unhealthy,
        CheckedAt = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromMilliseconds(12),
        Entries =
        [
            new HealthCheckEntrySnapshot
            {
                Name = "self",
                Status = HealthCheckState.Healthy,
                Description = "Host is responsive",
                Duration = TimeSpan.FromMilliseconds(1),
                Tags = ["live", "ready"],
                Data = new Dictionary<string, string>()
            },
            new HealthCheckEntrySnapshot
            {
                Name = "database",
                Status = HealthCheckState.Unhealthy,
                Description = "Database is unavailable",
                Duration = TimeSpan.FromMilliseconds(11),
                Tags = ["ready"],
                Data = new Dictionary<string, string> { ["endpoint"] = "primary" }
            }
        ]
    };
}
