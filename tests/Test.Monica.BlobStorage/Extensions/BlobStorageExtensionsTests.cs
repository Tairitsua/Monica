using Monica.BlobStorage.Abstractions;
using Monica.BlobStorage.Extensions;
using Xunit;

namespace Test.Monica.BlobStorage.Extensions;

public sealed class BlobStorageExtensionsTests
{
    [Fact]
    public async Task SaveAsync_WhenSavingBytes_ShouldForwardOverwriteAndCancellationAndDisposeIntermediateStream()
    {
        var storage = new RecordingStorage();
        await storage.SaveAsync("attachment", new byte[] { 1, 2, 3 }, true, TestContext.Current.CancellationToken);

        Assert.Equal("attachment", storage.Name);
        Assert.True(storage.Overwrite);
        Assert.Equal(TestContext.Current.CancellationToken, storage.Cancellation);
        Assert.Equal(new byte[] { 1, 2, 3 }, storage.Bytes);
        Assert.NotNull(storage.Input);
        Assert.False(storage.Input.CanRead);
    }

    [Fact]
    public async Task GetAllBytesOrNullAsync_WhenContentExists_ShouldReturnContentAndDisposeProviderStream()
    {
        var content = new MemoryStream([7, 8, 9]);
        var storage = new RecordingStorage { Output = content };

        var result = await storage.GetAllBytesOrNullAsync("attachment", TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 7, 8, 9 }, result);
        Assert.False(content.CanRead);
        Assert.Equal(TestContext.Current.CancellationToken, storage.Cancellation);
    }

    [Fact]
    public async Task GetAllBytesOrNullAsync_WhenObjectIsMissing_ShouldReturnNull()
    {
        Assert.Null(await new RecordingStorage().GetAllBytesOrNullAsync("missing", TestContext.Current.CancellationToken));
    }

    private sealed class RecordingStorage : IBlobStorage
    {
        public string? Name { get; private set; }
        public Stream? Input { get; private set; }
        public Stream? Output { get; init; }
        public byte[]? Bytes { get; private set; }
        public bool Overwrite { get; private set; }
        public CancellationToken Cancellation { get; private set; }

        public async Task SaveAsync(string name, Stream content, bool overrideExisting = false,
            CancellationToken cancellationToken = default)
        {
            Name = name;
            Input = content;
            Overwrite = overrideExisting;
            Cancellation = cancellationToken;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Bytes = copy.ToArray();
        }

        public Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
        {
            Cancellation = cancellationToken;
            return Task.FromResult(Output);
        }

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
