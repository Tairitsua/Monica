using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Minio;
using Monica.BlobStorage.Abstractions;
using Monica.BlobStorage.Minio.Providers;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Annotations;
using Monica.Core.Modularity.Models;

namespace Monica.Modules;

/// <summary>Selects Minio as the host's blob storage provider.</summary>
public static class ModuleMinioBlobStorageBuilderExtensions
{
    /// <summary>Uses one host-owned Minio client; registration performs no network I/O or bucket creation.</summary>
    /// <remarks>
    /// Configure endpoint and bucket before starting the host. Credentials are required when storage is first resolved,
    /// allowing hosts that never consume blob storage to compose without credentials. Options are fixed for its lifetime.
    /// </remarks>
    public static ModuleRegistration<ModuleMinioBlobStorage, ModuleMinioBlobStorageOption> UseMinio(
        this ModuleRegistration<ModuleBlobStorage, ModuleBlobStorageOption> registration,
        Action<ModuleMinioBlobStorageOption> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        registration.SatisfyFeature(ModuleBlobStorage.PROVIDER_FEATURE);
        return registration.Include<ModuleMinioBlobStorage, ModuleMinioBlobStorageOption>(configure);
    }
}

/// <summary>Registers a singleton Minio client and object storage provider.</summary>
public sealed class ModuleMinioBlobStorage : MonicaModule<ModuleMinioBlobStorageOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
        => module.Require<ModuleBlobStorage, ModuleBlobStorageOption>();

    /// <inheritdoc />
    public override void ValidateOptions(ModuleMinioBlobStorageOption options, string? profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName);
        ArgumentNullException.ThrowIfNull(options.ObjectKeyPrefix);
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleMinioBlobStorageOption> context)
    {
        context.Services.TryAddSingleton<IMinioClient>(services =>
        {
            var options = services.GetRequiredService<IOptions<ModuleMinioBlobStorageOption>>().Value;
            ArgumentException.ThrowIfNullOrWhiteSpace(options.AccessKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.SecretKey);
            return new MinioClient()
                .WithEndpoint(options.Endpoint)
                .WithCredentials(options.AccessKey, options.SecretKey)
                .WithSSL(options.UseSsl)
                .Build();
        });
        context.Services.AddSingleton<IBlobStorage, MinioBlobStorage>();
    }
}

/// <summary>Connection and namespace settings for one Minio bucket.</summary>
public sealed class ModuleMinioBlobStorageOption : ModuleOptions<ModuleMinioBlobStorage>
{
    /// <summary>Required Minio endpoint in host[:port] form; no default connection is selected.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Access key required when storage is first resolved. Supply it through the host's credential configuration.</summary>
    [ModuleOptionDiagnosticsSensitive]
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret key required when storage is first resolved. Supply it through the host's credential configuration.</summary>
    [ModuleOptionDiagnosticsSensitive]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Required bucket name, passed unchanged to the SDK for validation; no case conversion or rewriting occurs.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>Enables TLS. Defaults to false; set explicitly for HTTPS endpoints.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Creates a missing bucket on a save. Defaults to false; reads and deletes never create buckets.</summary>
    public bool CreateBucketIfNotExists { get; set; }

    /// <summary>Literal prefix prepended to every object key, without an added separator. Defaults to empty.</summary>
    /// <remarks>Changing this value changes the storage namespace and does not migrate existing objects.</remarks>
    public string ObjectKeyPrefix { get; set; } = string.Empty;
}
