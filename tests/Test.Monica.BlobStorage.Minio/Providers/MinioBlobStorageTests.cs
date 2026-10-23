using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Minio.Exceptions;
using Monica.BlobStorage.Abstractions;
using Monica.BlobStorage.Exceptions;
using NSubstitute;
using Test.Monica.BlobStorage.Minio.Support;
using Xunit;

namespace Test.Monica.BlobStorage.Minio.Providers;

public sealed class MinioBlobStorageTests
{
    [Fact]
    public async Task UseMinio_WhenCredentialsAreAbsent_ShouldAllowCompositionButRejectStorageActivation()
    {
        await using var app = await new MinioStorageFactory(configure: options =>
        {
            options.AccessKey = string.Empty;
            options.SecretKey = string.Empty;
        }).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.ThrowsAny<ArgumentException>(() => app.Services.GetRequiredService<IBlobStorage>());
    }

    [Fact]
    public async Task ReadOperations_WhenBucketIsMissing_ShouldReturnAbsenceWithoutCreatingIt()
    {
        var client = Substitute.For<IMinioClient>();
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>()).Returns(false);
        await using var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var storage = app.Services.GetRequiredService<IBlobStorage>();

        Assert.False(await storage.ExistsAsync("missing", TestContext.Current.CancellationToken));
        Assert.False(await storage.DeleteAsync("missing", TestContext.Current.CancellationToken));
        Assert.Null(await storage.GetOrNullAsync("missing", TestContext.Current.CancellationToken));

        await client.DidNotReceive().MakeBucketAsync(Arg.Any<MakeBucketArgs>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_WhenObjectExistsAndOverwriteIsOmitted_ShouldRejectWithoutWritingOrClosingInput()
    {
        var client = ExistingObjectClient();
        await using var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var content = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<BlobAlreadyExistsException>(() => app.Services.GetRequiredService<IBlobStorage>()
            .SaveAsync("existing", content, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(content.CanRead);
        await client.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_WhenOverwriteIsEnabled_ShouldForwardCancellationAndLeaveInputOpen()
    {
        var client = ExistingObjectClient();
        client.PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PutObjectResponse>(null!));
        await using var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var content = new MemoryStream([1, 2, 3]);

        await app.Services.GetRequiredService<IBlobStorage>().SaveAsync("existing", content, true, TestContext.Current.CancellationToken);

        Assert.True(content.CanRead);
        await client.Received(1).PutObjectAsync(Arg.Any<PutObjectArgs>(), TestContext.Current.CancellationToken);
        await client.DidNotReceive().StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().MakeBucketAsync(Arg.Any<MakeBucketArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_WhenBucketCreationIsEnabled_ShouldCreateMissingBucketBeforeWriting()
    {
        var client = Substitute.For<IMinioClient>();
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>()).Returns(false);
        await using var app = await new MinioStorageFactory(() => client, options => options.CreateBucketIfNotExists = true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var content = new MemoryStream([4]);

        await app.Services.GetRequiredService<IBlobStorage>().SaveAsync("new", content, cancellationToken: TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            client.MakeBucketAsync(Arg.Any<MakeBucketArgs>(), TestContext.Current.CancellationToken);
            client.PutObjectAsync(Arg.Any<PutObjectArgs>(), TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task SaveAsync_WhenTwoFirstWritersCreateBucketConcurrently_ShouldAllowBothUploads()
    {
        var client = Substitute.For<IMinioClient>();
        var bothChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var created = 0;
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var check = Interlocked.Increment(ref checks);
            if (check <= 2)
            {
                if (check == 2) bothChecked.SetResult();
                await bothChecked.Task.WaitAsync(call.Arg<CancellationToken>());
                return false;
            }

            return Volatile.Read(ref created) == 1;
        });
        client.MakeBucketAsync(Arg.Any<MakeBucketArgs>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Interlocked.Exchange(ref created, 1) == 0
                ? Task.CompletedTask
                : Task.FromException(new ArgumentException("Bucket already owned by you: test-bucket", "response")));
        await using var app = await new MinioStorageFactory(() => client, options => options.CreateBucketIfNotExists = true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var storage = app.Services.GetRequiredService<IBlobStorage>();
        using var first = new MemoryStream([1]);
        using var second = new MemoryStream([2]);

        await Task.WhenAll(
            storage.SaveAsync("first", first, true, TestContext.Current.CancellationToken),
            storage.SaveAsync("second", second, true, TestContext.Current.CancellationToken));

        await client.Received(2).MakeBucketAsync(Arg.Any<MakeBucketArgs>(), TestContext.Current.CancellationToken);
        await client.Received(2).PutObjectAsync(Arg.Any<PutObjectArgs>(), TestContext.Current.CancellationToken);
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task SaveAsync_WhenBucketCreationConflictRemainsMissing_ShouldPreserveOriginalFailure()
    {
        var client = Substitute.For<IMinioClient>();
        var failure = new ArgumentException("Bucket already owned by you: test-bucket", "response");
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>()).Returns(false);
        client.MakeBucketAsync(Arg.Any<MakeBucketArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));
        await using var app = await new MinioStorageFactory(() => client, options => options.CreateBucketIfNotExists = true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var content = new MemoryStream([1]);

        var actual = await Assert.ThrowsAsync<ArgumentException>(() => app.Services.GetRequiredService<IBlobStorage>()
            .SaveAsync("new", content, true, TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        await client.Received(2).BucketExistsAsync(Arg.Any<BucketExistsArgs>(), TestContext.Current.CancellationToken);
        await client.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveAsync_WhenBucketCreationFailsForAnotherReason_ShouldNotTreatExistingBucketAsSuccess()
    {
        var client = Substitute.For<IMinioClient>();
        var failure = new ArgumentException("Invalid bucket configuration.");
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>()).Returns(false, true);
        client.MakeBucketAsync(Arg.Any<MakeBucketArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));
        await using var app = await new MinioStorageFactory(() => client, options => options.CreateBucketIfNotExists = true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var content = new MemoryStream([1]);

        Assert.Same(failure, await Assert.ThrowsAsync<ArgumentException>(() => app.Services.GetRequiredService<IBlobStorage>()
            .SaveAsync("new", content, true, TestContext.Current.CancellationToken)));

        await client.Received(1).BucketExistsAsync(Arg.Any<BucketExistsArgs>(), TestContext.Current.CancellationToken);
        await client.DidNotReceive().PutObjectAsync(Arg.Any<PutObjectArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExistsAsync_WhenObjectIsMissing_ShouldReturnFalseButPreserveOtherBackendFailures()
    {
        var client = ExistingObjectClient();
        client.StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ObjectStat>(new ObjectNotFoundException()));
        await using var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var storage = app.Services.GetRequiredService<IBlobStorage>();
        Assert.False(await storage.ExistsAsync("missing", TestContext.Current.CancellationToken));

        var failure = new IOException("unavailable");
        client.StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ObjectStat>(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => storage.ExistsAsync("object", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task DeleteAsync_WhenObjectDisappearsAfterExistenceCheck_ShouldReturnFalse()
    {
        var client = ExistingObjectClient();
        client.RemoveObjectAsync(Arg.Any<RemoveObjectArgs>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ObjectNotFoundException()));
        await using var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(await app.Services.GetRequiredService<IBlobStorage>().DeleteAsync("object", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistsAsync_WhenAlreadyCanceled_ShouldNotCallBackend()
    {
        var client = Substitute.For<IMinioClient>();
        await using var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.Services.GetRequiredService<IBlobStorage>()
            .ExistsAsync("object", cancellation.Token));

        await client.DidNotReceive().BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UseMinio_WhenHostIsDisposed_ShouldDisposeItsSingleClient()
    {
        var client = Substitute.For<IMinioClient>();
        await using (var app = await new MinioStorageFactory(() => client).CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            var storage = app.Services.GetRequiredService<IBlobStorage>();
            using var scope = app.Services.CreateScope();
            Assert.Same(storage, scope.ServiceProvider.GetRequiredService<IBlobStorage>());
            client.DidNotReceive().Dispose();
        }

        client.Received(1).Dispose();
    }

    private static IMinioClient ExistingObjectClient()
    {
        var client = Substitute.For<IMinioClient>();
        client.BucketExistsAsync(Arg.Any<BucketExistsArgs>(), Arg.Any<CancellationToken>()).Returns(true);
        client.StatObjectAsync(Arg.Any<StatObjectArgs>(), Arg.Any<CancellationToken>())
            .Returns(ObjectStat.FromResponseHeaders("object", new Dictionary<string, string>()));
        return client;
    }
}
