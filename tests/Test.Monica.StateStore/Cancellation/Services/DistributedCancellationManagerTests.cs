using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.StateStore.Cancellation.Services;
using Monica.StateStore.TaskProgress.Models;
using Monica.StateStore.TaskProgress.Services;
using Monica.Testing.Doubles;
using Xunit;

namespace Test.Monica.StateStore.Cancellation.Services;

public sealed class DistributedCancellationManagerTests
{
    [Fact]
    public async Task ResetTokenAsync_WhenKeyIsNew_ShouldCreateSharedStateBeforeProgressStarts()
    {
        const string key = "new-progress";
        var store = new InMemoryDistributedStateStore();
        var manager = CreateManager(store);
        using var progressService = new TaskProgressService(store, manager, NullLogger<TaskProgressService>.Instance);
        try
        {
            await manager.ResetTokenAsync(key, TestContext.Current.CancellationToken);

            // Reset must establish a live shared token, not only a local source that polling treats as deleted.
            (await store.ExistAsync("DistributedCancellation:" + key, TestContext.Current.CancellationToken)).Should().BeTrue();
            var progress = await progressService.CreateTaskProgressAsync(key, options => options.TotalSteps = 100);
            await progress.UpdateStatusAsync(1, "Started");
            progress.CancellationToken.IsCancellationRequested.Should().BeFalse();
            await progress.CompleteTaskAsync("Completed");
        }
        finally
        {
            await manager.DeleteTokenAsync(key, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetTokenAsync_WhenTokenIsNewOrCancelled_ShouldAllowRemoteCancellation(bool cancelPrevious)
    {
        const string key = "cross-instance-progress";
        var store = new InMemoryDistributedStateStore();
        var writer = CreateManager(store);
        var remote = CreateManager(store);
        try
        {
            if (cancelPrevious)
            {
                var previous = await writer.GetOrCreateTokenAsync(key, TestContext.Current.CancellationToken);
                await writer.CancelTokenAsync(key, TestContext.Current.CancellationToken);
                previous.IsCancellationRequested.Should().BeTrue();
            }

            await writer.ResetTokenAsync(key, TestContext.Current.CancellationToken);
            var token = await writer.GetOrCreateTokenAsync(key, TestContext.Current.CancellationToken);
            token.IsCancellationRequested.Should().BeFalse();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => cancelled.TrySetResult());

            await remote.CancelTokenAsync(key, TestContext.Current.CancellationToken);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            token.IsCancellationRequested.Should().BeTrue();
            (await writer.IsCancelledAsync(key, TestContext.Current.CancellationToken)).Should().BeTrue();
        }
        finally
        {
            await writer.DeleteTokenAsync(key, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CreateOrResumeTaskProgressAsync_WhenCompletedIdIsReused_ShouldObserveCancellationInNewRun()
    {
        const string key = "completed-progress";
        var store = new InMemoryDistributedStateStore();
        var writer = CreateManager(store);
        var remote = CreateManager(store);
        using var service = new TaskProgressService(store, writer, NullLogger<TaskProgressService>.Instance);
        try
        {
            var first = await service.CreateOrResumeTaskProgressAsync(key, options => options.TotalSteps = 100);
            await first.CompleteTaskAsync("First run completed");

            var second = await service.CreateOrResumeTaskProgressAsync(key, options => options.TotalSteps = 100);
            await second.UpdateStatusAsync(1, "New run started");
            second.IsCancelled.Should().BeFalse();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = second.CancellationToken.Register(() => cancelled.TrySetResult());

            await remote.CancelTokenAsync(key, TestContext.Current.CancellationToken);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            second.IsCancelled.Should().BeTrue();
        }
        finally
        {
            await writer.DeleteTokenAsync(key, TestContext.Current.CancellationToken);
        }
    }

    private static DistributedCancellationManager CreateManager(InMemoryDistributedStateStore store)
        => new(store, NullLogger<DistributedCancellationManager>.Instance,
            Options.Create(new ModuleCancellationManagerOption { PollingIntervalMs = 10 }));
}
