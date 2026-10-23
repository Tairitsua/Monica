using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Monica.BlobStorage.Abstractions;
using Test.Monica.BlobStorage.Minio.Support;
using Xunit;

namespace Test.Monica.BlobStorage.Minio.Providers;

public sealed class MinioSdkContractTests
{
    [Fact]
    public async Task Storage_WhenUsingNativeSdk_ShouldPreservePrefixRemainingBytesStreamOwnershipAndDownloadPosition()
    {
        var handler = new ObjectStorageHandler();
        var factory = new MinioStorageFactory(() => new MinioClient()
            .WithEndpoint("127.0.0.1:9000")
            .WithCredentials("test-access", "test-secret")
            .WithRegion("us-east-1")
            .WithHttpClient(new HttpClient(handler), disposeHttpClient: true)
            .Build());

        await using (var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            var storage = app.Services.GetRequiredService<IBlobStorage>();
            using var content = new MemoryStream([1, 2, 3]);
            content.Position = 1;
            await storage.SaveAsync("temporary/content", content, true, TestContext.Current.CancellationToken);

            Assert.True(content.CanRead);
            var upload = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Put);
            Assert.Equal("/test-bucket/host/temporary/content", upload.Path);
            Assert.Equal(new byte[] { 2, 3 }, upload.Content);

            await using var download = await storage.GetOrNullAsync("temporary/content", TestContext.Current.CancellationToken);
            Assert.NotNull(download);
            Assert.Equal(0, download.Position);
            using var copy = new MemoryStream();
            await download.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(ObjectStorageHandler.DOWNLOAD, copy.ToArray());
            Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get &&
                request.Path == "/test-bucket/host/temporary/content");
            Assert.False(handler.Disposed);
        }

        Assert.True(handler.Disposed);
    }

    private sealed class ObjectStorageHandler : HttpMessageHandler
    {
        public static readonly byte[] DOWNLOAD = [9, 8, 7];
        public List<Request> Requests { get; } = [];
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Request(request.Method, request.RequestUri!.AbsolutePath, bytes));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(request.Method == HttpMethod.Get ? DOWNLOAD : [])
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"test-etag\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            if (request.Method == HttpMethod.Head) response.Content.Headers.ContentLength = DOWNLOAD.Length;
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record Request(HttpMethod Method, string Path, byte[] Content);
}
