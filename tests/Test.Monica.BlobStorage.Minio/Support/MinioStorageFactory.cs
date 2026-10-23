using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minio;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;

namespace Test.Monica.BlobStorage.Minio.Support;

internal sealed class MinioStorageFactory(
    Func<IMinioClient>? clientFactory = null,
    Action<ModuleMinioBlobStorageOption>? configure = null)
    : MonicaTestApplicationFactory<MinioStorageFactory>
{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
        builder.AddBlobStorage().UseMinio(options =>
        {
            options.Endpoint = "127.0.0.1:9000";
            options.AccessKey = "test-access";
            options.SecretKey = "test-secret";
            options.BucketName = "test-bucket";
            options.ObjectKeyPrefix = "host/";
            configure?.Invoke(options);
        });
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        if (clientFactory is not null)
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton(_ => clientFactory());
        }
    }
}
