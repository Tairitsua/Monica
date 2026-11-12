using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Support;
using Monica.Modules;
using Monica.Testing.Results;
using Xunit;

namespace Test.Monica.Framework.Logging.UILogging.Support;

/// <summary>
/// Verifies log-directory containment independently of the HTTP and filesystem boundaries.
/// </summary>
public sealed class LogFileQueryServiceTests
{
    [Fact]
    public void ResolveFilePath_WhenSiblingDirectorySharesRootPrefix_ShouldRejectTraversalAndAcceptNestedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "monica-containment", "logs");
        var service = new LogFileQueryService(
            Options.Create(new ModuleLoggingOption()),
            Options.Create(new ModuleLoggingUIOption { LogDirectory = root }),
            NullLogger<LogFileQueryService>.Instance);

        var traverseSibling = () => service.ResolveFilePath("../logs-backup/private.log");

        traverseSibling.Should().Throw<InvalidOperationException>();
        service.ResolveFilePath("archive/日志 %20 #.log")
            .Should().Be(Path.GetFullPath(Path.Combine(root, "archive", "日志 %20 #.log")));
    }

    [Fact]
    public async Task ListFilesAsync_WhenFilesHaveDuplicateNames_ShouldPreserveTheirDistinctRelativePathsAndExactByteSizes()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var archive = Path.Combine(factory.LogDirectory, "archive");
        Directory.CreateDirectory(archive);
        byte[] current = [0, 1, 2, 3];
        byte[] old = [4, 5, 6];
        await File.WriteAllBytesAsync(Path.Combine(factory.LogDirectory, "same.log"), current, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(archive, "same.log"), old, TestContext.Current.CancellationToken);
        var service = CreateService(factory.LogDirectory);

        var files = (await service.ListFilesAsync(TestContext.Current.CancellationToken)).ShouldSucceed()!;

        files.Should().HaveCount(2);
        files.Should().ContainSingle(static file => file.RelativePath == "same.log").Which.Size.Should().Be(current.Length);
        files.Should().ContainSingle(static file => file.RelativePath == "archive/same.log").Which.Size.Should().Be(old.Length);
        files.Should().OnlyContain(static file => file.FileName == "same.log");
    }

    [Fact]
    public async Task OpenFileAsync_WhenTheCallerDisposesItsDownload_ShouldReleaseTheHandleAndPreserveExactBytes()
    {
        using var factory = new LoggingUITestApplicationFactory();
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'%', (byte)'2', (byte)'0', (byte)'\r', (byte)'\n', 0xE6, 0x97, 0xA5];
        var path = Path.Combine(factory.LogDirectory, "日志 %20 #.log");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var service = CreateService(factory.LogDirectory);

        await using (var stream = (await service.OpenFileAsync("日志 %20 #.log", TestContext.Current.CancellationToken)).ShouldSucceed()!)
        {
            using var destination = new MemoryStream();
            await stream.CopyToAsync(destination, TestContext.Current.CancellationToken);
            destination.ToArray().Should().Equal(bytes);
        }

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        exclusive.Length.Should().Be(bytes.Length);
    }

    [Theory]
    [InlineData("../outside.log")]
    [InlineData("../logs-backup/private.log")]
    public async Task OpenFileAsync_WhenThePathEscapesTheRegisteredRoot_ShouldReturnFailureWithoutOpeningAStream(string relativePath)
    {
        using var factory = new LoggingUITestApplicationFactory();
        var service = CreateService(factory.LogDirectory);

        var result = await service.OpenFileAsync(relativePath, TestContext.Current.CancellationToken);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().NotBeNullOrEmpty();
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task ListFilesAndOpenFileAsync_WhenCanceledBeforeAdmission_ShouldPropagateCancellationWithoutOwningAStream()
    {
        using var factory = new LoggingUITestApplicationFactory();
        await File.WriteAllTextAsync(Path.Combine(factory.LogDirectory, "existing.log"), "existing\n", TestContext.Current.CancellationToken);
        var service = CreateService(factory.LogDirectory);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await canceled.CancelAsync();

        var list = () => service.ListFilesAsync(canceled.Token);
        var open = () => service.OpenFileAsync("existing.log", canceled.Token);

        await list.Should().ThrowAsync<OperationCanceledException>();
        await open.Should().ThrowAsync<OperationCanceledException>();
        using var exclusive = new FileStream(Path.Combine(factory.LogDirectory, "existing.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        exclusive.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadContextAsync_WhenAnAbsolutePathIsOutsideTheConfiguredDirectory_ShouldRejectItWithoutReadingItsLines()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var service = CreateService(Path.Combine(factory.LogDirectory, "allowed"));
        var outsidePath = Path.Combine(factory.LogDirectory, "outside.log");
        await File.WriteAllTextAsync(outsidePath, "outside evidence\n", TestContext.Current.CancellationToken);

        var result = await service.ReadContextAsync(outsidePath, 1, 1, TestContext.Current.CancellationToken);

        result.Status.Should().NotBe(ResultStatus.Ok);
        result.Message.Should().NotBeNullOrEmpty();
        result.Data.Lines.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ListFilesAsync_WhenTheConfiguredDirectoryIsMissing_ShouldFailInsteadOfClaimingAnEmptySuccessfulCatalog()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var directory = Path.Combine(factory.LogDirectory, "catalog");
        var service = CreateService(directory);

        var missing = await service.ListFilesAsync(TestContext.Current.CancellationToken);
        missing.Status.Should().NotBe(ResultStatus.Ok);
        missing.Message.Should().NotBeNullOrEmpty();
        missing.Data.Should().BeNull();

        Directory.CreateDirectory(directory);
        var empty = (await service.ListFilesAsync(TestContext.Current.CancellationToken)).ShouldSucceed()!;
        empty.Should().BeEmpty();
    }

    [Fact]
    public async Task ListFilesAsync_WhenTheDirectoryDisappearsAfterASuccessfulRead_ShouldReturnFailureAndPreserveThePreviousSnapshot()
    {
        using var factory = new LoggingUITestApplicationFactory();
        var directory = Path.Combine(factory.LogDirectory, "catalog");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "previous.log");
        await File.WriteAllTextAsync(file, "retained bytes\n", TestContext.Current.CancellationToken);
        var service = CreateService(directory);
        var previous = (await service.ListFilesAsync(TestContext.Current.CancellationToken)).ShouldSucceed()!;
        File.Delete(file);
        Directory.Delete(directory); // The scenario-owned directory is empty; no recursive deletion is needed.

        var current = await service.ListFilesAsync(TestContext.Current.CancellationToken);

        current.Status.Should().NotBe(ResultStatus.Ok);
        current.Message.Should().NotBeNullOrEmpty();
        current.Data.Should().BeNull();
        previous.Should().ContainSingle().Which.RelativePath.Should().Be("previous.log");
        previous[0].Size.Should().BeGreaterThan(0);
    }

    private static LogFileQueryService CreateService(string root) =>
        new(Options.Create(new ModuleLoggingOption()), Options.Create(new ModuleLoggingUIOption { LogDirectory = root }), NullLogger<LogFileQueryService>.Instance);
}
