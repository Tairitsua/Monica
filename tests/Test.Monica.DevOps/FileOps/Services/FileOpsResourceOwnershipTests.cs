using System.IO.Compression;
using System.Text;
using Monica.Core.Results;
using Monica.DevOps.FileOps.Models;
using Test.Monica.DevOps.FileOps.Support;
using Xunit;

namespace Test.Monica.DevOps.FileOps.Services;

public sealed class FileOpsResourceOwnershipTests
{
    [Fact]
    public async Task OpenDownloadAsync_WhenCallerDisposesDescriptorStream_ShouldReleaseFileWithoutDeletingSource()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var source = Path.Combine(fixture.Root, "harmless-download.txt");
        var expected = Encoding.UTF8.GetBytes("Harmless real file bytes.");
        await File.WriteAllBytesAsync(source, expected, TestContext.Current.CancellationToken);

        var result = await fixture.Facade.OpenDownloadAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Null(result.Message);
        var descriptor = Assert.IsType<FileOpsDownloadDescriptor>(result.Data);
        try
        {
            Assert.Equal(source, descriptor.FullPath);
            Assert.Equal("harmless-download.txt", descriptor.FileName);
            Assert.Equal(expected.LongLength, descriptor.SizeBytes);
            Assert.True(descriptor.ContentStream.CanRead);
            await using var copied = new MemoryStream();
            await descriptor.ContentStream.CopyToAsync(copied, TestContext.Current.CancellationToken);
            Assert.Equal(expected, copied.ToArray());
        }
        finally { await descriptor.ContentStream.DisposeAsync(); }

        Assert.False(descriptor.ContentStream.CanRead);
        Assert.True(File.Exists(source));
        await using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(expected.LongLength, exclusive.Length);
    }

    [Fact]
    public async Task OpenArchiveDownloadAsync_WhenCallerDisposesOwnedStream_ShouldDeleteItsTemporaryZipAndRetainRecursiveVisibleContent()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var source = Path.Combine(fixture.Root, "payload");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "root.txt"), "Root harmless content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "child.txt"), "Nested harmless content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, ".hidden.txt"), "Hidden harmless content", TestContext.Current.CancellationToken);

        var result = await fixture.Facade.OpenArchiveDownloadAsync(new FileOpsArchiveDownloadRequest
        {
            Paths = [source], ArchiveName = "harmless-bundle"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, result.Status);
        Assert.Null(result.Message);
        var descriptor = Assert.IsType<FileOpsDownloadDescriptor>(result.Data);
        var ownedArchivePath = descriptor.FullPath;
        try
        {
            Assert.Equal("harmless-bundle.zip", descriptor.FileName);
            Assert.Equal("application/zip", descriptor.ContentType);
            Assert.Equal(descriptor.ContentStream.Length, descriptor.SizeBytes);
            using var archive = new ZipArchive(descriptor.ContentStream, ZipArchiveMode.Read, leaveOpen: true);
            Assert.Equal(new[] { "payload/", "payload/empty/", "payload/nested/", "payload/nested/child.txt", "payload/root.txt" },
                archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
            using var childReader = new StreamReader(archive.GetEntry("payload/nested/child.txt")!.Open(), Encoding.UTF8);
            Assert.Equal("Nested harmless content", await childReader.ReadToEndAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, archive.GetEntry("payload/empty/")!.Length);
        }
        finally
        {
            // The descriptor owns this exact temporary path through DeleteOnClose; no global temp sweep is needed.
            await descriptor.ContentStream.DisposeAsync();
        }

        Assert.False(descriptor.ContentStream.CanRead);
        Assert.False(File.Exists(ownedArchivePath));
        Assert.True(File.Exists(Path.Combine(source, "nested", "child.txt")));
        Assert.True(File.Exists(Path.Combine(source, ".hidden.txt")));
    }
}
