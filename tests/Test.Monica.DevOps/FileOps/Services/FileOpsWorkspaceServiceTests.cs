using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core.Results;
using Monica.DevOps.FileOps.Abstractions;
using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.Services;
using Monica.DevOps.FileOps.Services.Support;
using Test.Monica.DevOps.FileOps.Support;
using Xunit;

namespace Test.Monica.DevOps.FileOps.Services;

public sealed class FileOpsWorkspaceServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteEntriesAsync_WhenSecondTargetIsMissingOrReadOnly_ShouldRetainEarlierDeletionAndLeaveLaterTarget(bool readOnlySecond)
    {
        using var fixture = new FileOpsFilesystemFixture();
        var first = Path.Combine(fixture.Root, "first.txt");
        var second = Path.Combine(fixture.Root, "second.txt");
        var third = Path.Combine(fixture.Root, "third.txt");
        await File.WriteAllTextAsync(first, "First harmless file", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(third, "Last harmless file", TestContext.Current.CancellationToken);
        if (readOnlySecond)
        {
            await File.WriteAllTextAsync(second, "Protected harmless file", TestContext.Current.CancellationToken);
        }
        fixture.Configure(config =>
        {
            config.AllowDeleteOperations = true;
            config.ReadOnlyPaths = readOnlySecond ? [second] : [];
        });

        var result = await fixture.Facade.DeleteEntriesAsync(new FileOpsBatchDeleteRequest
        {
            Paths = [first, second, third], Recursive = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.BadRequest, result.Status);
        Assert.Contains(readOnlySecond ? "GeneratedMessages:PathReadOnly" : "GeneratedMessages:EntryNotFound", result.Message);
        Assert.Contains(second, result.Message);
        Assert.Null(result.Data);
        Assert.False(File.Exists(first));
        Assert.Equal(readOnlySecond, File.Exists(second));
        Assert.Equal("Last harmless file", await File.ReadAllTextAsync(third, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEntriesAsync_WhenParentPrecedesItsChild_ShouldFailAfterAppliedParentDeletionWithoutDeletingLaterFile()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var parent = Path.Combine(fixture.Root, "parent");
        Directory.CreateDirectory(parent);
        var child = Path.Combine(parent, "child.txt");
        var later = Path.Combine(fixture.Root, "later.txt");
        await File.WriteAllTextAsync(child, "Nested harmless file", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(later, "Later harmless file", TestContext.Current.CancellationToken);
        fixture.Configure(config => config.AllowDeleteOperations = true);

        var result = await fixture.Facade.DeleteEntriesAsync(new FileOpsBatchDeleteRequest
        {
            Paths = [parent, child, later], Recursive = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.BadRequest, result.Status);
        Assert.Contains("GeneratedMessages:EntryNotFound", result.Message);
        Assert.Contains(child, result.Message);
        Assert.Null(result.Data);
        Assert.False(Directory.Exists(parent));
        Assert.True(File.Exists(later));
    }

    [Fact]
    public async Task DeleteEntriesAsync_WhenPathsContainDuplicatesAndBlanks_ShouldDeleteEachDistinctTargetOnce()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "target.txt");
        await File.WriteAllTextAsync(target, "Harmless content", TestContext.Current.CancellationToken);
        fixture.Configure(config => config.AllowDeleteOperations = true);

        var result = await fixture.Facade.DeleteEntriesAsync(new FileOpsBatchDeleteRequest
        {
            Paths = [target, string.Empty, "  ", $"  {target}  ", target], Recursive = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Null(result.Message);
        var data = Assert.IsType<FileOpsBatchDeleteResult>(result.Data);
        Assert.Equal(target, Assert.Single(data.DeletedEntries).Path);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task DeleteEntriesAsync_WhenPolicySourceChangesBetweenReads_ShouldUseOneSnapshotForBatchAndFreshPolicyForNextBatch()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var first = Path.Combine(fixture.Root, "first.txt");
        var second = Path.Combine(fixture.Root, "second.txt");
        var later = Path.Combine(fixture.Root, "later.txt");
        await File.WriteAllTextAsync(first, "First harmless file", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "Second harmless file", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(later, "Later harmless file", TestContext.Current.CancellationToken);
        var store = new ChangingDeletePolicyStore(fixture.Store.GetCurrent());
        var workspace = new FileOpsWorkspaceService(store, new FileOpsPathPolicy(), new FileOpsTextInspector(),
            NullLogger<FileOpsWorkspaceService>.Instance);

        var completed = await workspace.DeleteEntriesAsync(new FileOpsBatchDeleteRequest
        {
            Paths = [first, second], Recursive = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Reads);
        Assert.Equal(new[] { first, second }, completed.DeletedEntries.Select(entry => entry.Path));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        var exception = await Assert.ThrowsAsync<FileOpsOperationException>(() => workspace.DeleteEntriesAsync(
            new FileOpsBatchDeleteRequest { Paths = [later] }, TestContext.Current.CancellationToken));
        Assert.Equal(FileOpsMessageCode.DeleteOperationsDisabled, exception.MessageCode);
        Assert.Equal(2, store.Reads);
        Assert.True(File.Exists(later));
    }

    private sealed class ChangingDeletePolicyStore(FileOpsRuntimeConfig config) : IFileOpsRuntimeConfigStore
    {
        public int Reads { get; private set; }

        public FileOpsRuntimeConfig GetCurrent()
        {
            var snapshot = config.Clone();
            snapshot.AllowDeleteOperations = ++Reads == 1;
            return snapshot;
        }

        public void Update(FileOpsRuntimeConfig runtimeConfig) => throw new InvalidOperationException("This scenario reads policy only.");
    }
}
