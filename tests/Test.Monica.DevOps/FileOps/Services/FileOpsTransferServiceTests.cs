using System.Text;
using Monica.Core.Results;
using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.FileOps.Models;
using Test.Monica.DevOps.FileOps.Support;
using Xunit;

namespace Test.Monica.DevOps.FileOps.Services;

public sealed class FileOpsTransferServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadAsync_WhenTargetIsConfiguredReadOnly_ShouldRejectBeforeChangingExistingOrCreatingMissingFile(bool exists)
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "protected.txt");
        byte[] original = Encoding.UTF8.GetBytes("Original harmless content.");
        if (exists)
        {
            await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        }

        fixture.Configure(config => config.ReadOnlyPaths = [target]);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Replacement content."));

        var result = await fixture.Facade.UploadAsync(fixture.Root,
            [new FileOpsUploadPayload("protected.txt", content.Length, content)], overwrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.BadRequest, result.Status);
        Assert.Equal($"ServiceMessages:UploadFailed [{fixture.Root}, GeneratedMessages:PathReadOnly [{target}]]", result.Message);
        Assert.Null(result.Data);
        Assert.Equal(0, content.Position);
        Assert.True(content.CanRead);
        Assert.Equal(exists, File.Exists(target));
        if (exists)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task UploadAsync_WhenSmallWritablePayloadCompletes_ShouldReturnMetadataOfClosedCreatedOrReplacedFile(
        bool exists, bool overwrite)
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "writable.txt");
        if (exists)
        {
            await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("Longer previous content that must be truncated."),
                TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(target, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }

        byte[] replacement = Encoding.UTF8.GetBytes("Small payload.");
        await using var content = new MemoryStream(replacement);

        var result = await fixture.Facade.UploadAsync(fixture.Root,
            [new FileOpsUploadPayload("writable.txt", content.Length, content)], overwrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Null(result.Message);
        var data = Assert.IsType<FileOpsUploadResult>(result.Data);
        Assert.Equal(fixture.Root, data.DirectoryPath);
        Assert.Equal(overwrite, data.Overwrite);
        var entry = Assert.Single(data.UploadedEntries);
        var disk = new FileInfo(target);
        Assert.Equal(target, entry.FullPath);
        Assert.Equal(FileOpsEntryKind.File, entry.Kind);
        Assert.Equal(replacement.LongLength, disk.Length);
        Assert.Equal(disk.Length, entry.SizeBytes);
        Assert.Equal(new DateTimeOffset(disk.LastWriteTimeUtc), entry.LastModifiedAt);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.True(entry.CanEditText);
        Assert.True(entry.CanDownload);
        Assert.False(entry.IsReadOnly);
        Assert.True(content.CanRead);

        // Exclusive access also verifies the destination stream is released when upload completes.
        await using var exclusive = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(replacement.LongLength, exclusive.Length);
    }

    [Theory]
    [InlineData(true, FileOpsMessageCode.PathReadOnly)]
    [InlineData(false, FileOpsMessageCode.WriteOperationsDisabled)]
    public async Task SaveUploadAsync_WhenParentWritesAreRestricted_ShouldRejectWithoutCreatingTarget(
        bool readOnlyParent, FileOpsMessageCode expectedCode)
    {
        using var fixture = new FileOpsFilesystemFixture();
        var parent = Path.Combine(fixture.Root, "parent");
        Directory.CreateDirectory(parent);
        fixture.Configure(config =>
        {
            config.ReadOnlyPaths = readOnlyParent ? [parent] : [];
            config.AllowWriteOperations = readOnlyParent;
        });
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Harmless upload."));

        var exception = await Assert.ThrowsAsync<FileOpsOperationException>(() => fixture.Transfer.SaveUploadAsync(
            parent, "new.txt", content.Length, content, overwrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.MessageCode);
        Assert.False(File.Exists(Path.Combine(parent, "new.txt")));
        Assert.Equal(0, content.Position);
    }

    [Fact]
    public async Task SaveUploadAsync_WhenWritableTargetExistsAndOverwriteIsFalse_ShouldPreserveOriginalBytes()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "existing.txt");
        byte[] original = Encoding.UTF8.GetBytes("Existing harmless content.");
        await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Replacement."));

        var exception = await Assert.ThrowsAsync<FileOpsOperationException>(() => fixture.Transfer.SaveUploadAsync(
            fixture.Root, "existing.txt", content.Length, content, overwrite: false, TestContext.Current.CancellationToken));

        Assert.Equal(FileOpsMessageCode.EntryAlreadyExists, exception.MessageCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(0, content.Position);
    }

}
