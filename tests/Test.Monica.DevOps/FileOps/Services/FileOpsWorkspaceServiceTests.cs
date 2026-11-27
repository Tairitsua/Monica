using System.Text;
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

    [Fact]
    public async Task BrowseAsync_WhenPreviewCapabilitiesDiffer_ShouldReportBoundedEvidence()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var token = TestContext.Current.CancellationToken;
        var binary = Path.Combine(fixture.Root, "binary.json");
        var oversized = Path.Combine(fixture.Root, "oversized.json");
        var unicode = Path.Combine(fixture.Root, "unicode.txt");
        var previewOnly = Path.Combine(fixture.Root, "preview.resx");
        await File.WriteAllBytesAsync(binary, new byte[] { 77, 90, 0, 0 }, token);
        await File.WriteAllTextAsync(oversized, new string('x', 4097), token);
        await File.WriteAllTextAsync(unicode, "text with a UTF-16 byte-order mark", Encoding.Unicode, token);
        await File.WriteAllTextAsync(previewOnly, "<root />", token);
        fixture.Configure(config => config.MaxTextFileSizeBytes = 4096);

        var result = await fixture.Workspace.BrowseAsync(fixture.Root, token);
        var entries = result.Entries.ToDictionary(entry => entry.FullPath);

        Assert.Equal(FileOpsTextPreviewKind.Binary, entries[binary].TextPreview);
        Assert.False(entries[binary].CanEditText);
        Assert.Equal(FileOpsTextPreviewKind.Oversized, entries[oversized].TextPreview);
        Assert.False(entries[oversized].CanEditText);
        Assert.Equal(FileOpsTextPreviewKind.Text, entries[previewOnly].TextPreview);
        Assert.False(entries[previewOnly].CanEditText);
        Assert.Equal(FileOpsTextPreviewKind.Text, entries[unicode].TextPreview);
        Assert.True(entries[unicode].CanEditText);
        Assert.Equal("text with a UTF-16 byte-order mark", (await fixture.Workspace.ReadTextFileAsync(unicode, token)).Content);
    }

    [Fact]
    public async Task BrowseAsync_WhenRootIsRepeatedWithTrailingSeparator_ShouldShowOneRootAndProtectNestedAllowedRoot()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var token = TestContext.Current.CancellationToken;
        var nested = Directory.CreateDirectory(Path.Combine(fixture.Root, "nested")).FullName;
        fixture.Configure(config =>
        {
            config.AllowDeleteOperations = true;
            config.AllowedRoots = [fixture.Root, fixture.Root + Path.DirectorySeparatorChar, nested];
        });

        var active = await fixture.Workspace.GetRuntimeConfigAsync(token);
        var result = await fixture.Workspace.BrowseAsync(fixture.Root, token);

        Assert.Equal(new[] { fixture.Root, nested }, active.AllowedRoots);
        Assert.False(Assert.Single(result.Entries).CanDelete);
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
