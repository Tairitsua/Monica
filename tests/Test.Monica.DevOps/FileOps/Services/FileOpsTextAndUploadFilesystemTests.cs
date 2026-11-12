using System.Text;
using Monica.Core.Results;
using Monica.DevOps.FileOps.Models;
using Test.Monica.DevOps.FileOps.Support;
using Xunit;

namespace Test.Monica.DevOps.FileOps.Services;

public sealed class FileOpsTextAndUploadFilesystemTests
{
    [Theory]
    [InlineData("utf-8")]
    [InlineData("unknown-fileops-test-encoding")]
    public async Task SaveTextFileAsync_WhenUtf8OrFallbackEncodingIsRequested_ShouldPersistRealTextAndReadFreshMetadata(string encodingName)
    {
        using var fixture = new FileOpsFilesystemFixture();
        var path = Path.Combine(fixture.Root, "rescue.txt");
        await File.WriteAllTextAsync(path, "Initial harmless text", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        const string CONTENT = "First line\nHarmless 中文 text\n";

        var saved = await fixture.Facade.SaveTextFileAsync(new FileOpsTextUpdateRequest
        {
            Path = path, Content = CONTENT, EncodingName = encodingName
        }, TestContext.Current.CancellationToken);
        var read = await fixture.Facade.ReadTextFileAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, saved.Status);
        Assert.Contains("Messages:TextFileSaved", saved.Message);
        Assert.Equal(ResultStatus.Ok, read.Status);
        Assert.Null(read.Message);
        var data = Assert.IsType<FileOpsFileTextContent>(read.Data);
        var disk = new FileInfo(path);
        Assert.Equal(CONTENT, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(CONTENT, data.Content);
        Assert.Equal("utf-8", data.EncodingName);
        Assert.Equal(3, data.LineCount);
        Assert.Equal(disk.Length, data.SizeBytes);
        Assert.Equal(new DateTimeOffset(disk.LastWriteTimeUtc), data.LastModifiedAt);
        Assert.True(data.CanSave);
    }

    [Fact]
    public async Task ReadAndSaveTextFileAsync_WhenTextExceedsConfiguredLimit_ShouldFailWithoutReplacingExistingText()
    {
        using var fixture = new FileOpsFilesystemFixture();
        fixture.Configure(config => config.MaxTextFileSizeBytes = 4096);
        var large = Path.Combine(fixture.Root, "large.txt");
        var retained = Path.Combine(fixture.Root, "retained.txt");
        var oversized = new string('x', 4097);
        await File.WriteAllTextAsync(large, oversized, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(retained, "retained harmless content", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var read = await fixture.Facade.ReadTextFileAsync(large, TestContext.Current.CancellationToken);
        var saved = await fixture.Facade.SaveTextFileAsync(new FileOpsTextUpdateRequest
        {
            Path = retained, Content = oversized, EncodingName = "utf-8"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.BadRequest, read.Status);
        Assert.Contains("GeneratedMessages:TextFileTooLarge", read.Message);
        Assert.Null(read.Data);
        Assert.Equal(ResultStatus.BadRequest, saved.Status);
        Assert.Contains("GeneratedMessages:TextFileTooLarge", saved.Message);
        Assert.Equal("retained harmless content", await File.ReadAllTextAsync(retained, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadTextFileAsync_WhenExtensionIsUnsupportedButBytesAreText_ShouldPreviewWhileSaveRemainsForbidden()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var path = Path.Combine(fixture.Root, "preview-only.bin");
        await File.WriteAllTextAsync(path, "Harmless plain text", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var read = await fixture.Facade.ReadTextFileAsync(path, TestContext.Current.CancellationToken);
        var saved = await fixture.Facade.SaveTextFileAsync(new FileOpsTextUpdateRequest
        {
            Path = path, Content = "Rejected replacement", EncodingName = "utf-8"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.Ok, read.Status);
        var data = Assert.IsType<FileOpsFileTextContent>(read.Data);
        Assert.Equal("Harmless plain text", data.Content);
        Assert.False(data.CanSave);
        Assert.Equal(ResultStatus.BadRequest, saved.Status);
        Assert.Contains("GeneratedMessages:EditableExtensionRequired", saved.Message);
        Assert.Equal("Harmless plain text", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UploadAsync_WhenCopyFailsAfterWritingPrefix_ShouldReturnFailureAndLeaveActualTruncatedPartialOutput()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var path = Path.Combine(fixture.Root, "partial.txt");
        await File.WriteAllTextAsync(path, "Original content which is replaced by a partial write", TestContext.Current.CancellationToken);
        await using var input = new FailingUploadInput();

        var result = await fixture.Facade.UploadAsync(fixture.Root,
            [new FileOpsUploadPayload("partial.txt", input.Length, input)], overwrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.BadRequest, result.Status);
        Assert.Contains("Controlled copy failure after prefix", result.Message);
        Assert.Null(result.Data);
        Assert.True(input.CanRead);
        Assert.Equal(FailingUploadInput.Prefix, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(FailingUploadInput.Prefix.LongLength, exclusive.Length);
    }

    private sealed class FailingUploadInput() : MemoryStream(Encoding.UTF8.GetBytes("Never fully copied content"))
    {
        public static byte[] Prefix { get; } = Encoding.UTF8.GetBytes("partial");

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(Prefix.AsMemory(), cancellationToken);
            throw new IOException("Controlled copy failure after prefix");
        }
    }
}
