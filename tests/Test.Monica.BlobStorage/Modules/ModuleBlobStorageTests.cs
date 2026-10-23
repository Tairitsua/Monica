using Microsoft.Extensions.DependencyInjection;
using Monica.BlobStorage.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.BlobStorage.Modules;

public sealed class ModuleBlobStorageTests
{
    [Fact]
    public async Task AddBlobStorage_WhenProviderIsSelected_ShouldShareAndDisposeItWithHost()
    {
        DisposableStorage storage;
        await using (var app = await new StorageFactory(true).CreateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            storage = Assert.IsType<DisposableStorage>(app.Services.GetRequiredService<IBlobStorage>());
            using var scope = app.Services.CreateScope();
            Assert.Same(storage, scope.ServiceProvider.GetRequiredService<IBlobStorage>());
            Assert.False(storage.Disposed);
        }

        Assert.True(storage.Disposed);
    }

    [Fact]
    public async Task AddBlobStorage_WhenProviderIsMissing_ShouldRejectIncompleteComposition()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var app = await new StorageFactory(false).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        });
    }

    private sealed class StorageFactory(bool selectProvider) : MonicaTestApplicationFactory<StorageFactory>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            var registration = builder.AddBlobStorage();
            if (selectProvider) registration.UseProvider<DisposableStorage>();
        }
    }

    public sealed class DisposableStorage : IBlobStorage, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public Task SaveAsync(string name, Stream content, bool overrideExisting = false, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
