using Microsoft.Extensions.DependencyInjection;
using Monica.BlobStorage.Abstractions;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;

namespace Monica.Modules;

/// <summary>Registers a single host-owned blob storage capability and selects its provider.</summary>
public static class ModuleBlobStorageBuilderExtensions
{
    /// <summary>Registers blob storage; select a provider before completing host composition.</summary>
    public static ModuleRegistration<ModuleBlobStorage, ModuleBlobStorageOption> AddBlobStorage(
        this IMonicaBuilder builder)
        => builder.AddModule<ModuleBlobStorage, ModuleBlobStorageOption>();

    /// <summary>Selects a singleton provider with singleton-compatible dependencies, owned by the host.</summary>
    public static ModuleRegistration<ModuleBlobStorage, ModuleBlobStorageOption> UseProvider<TProvider>(
        this ModuleRegistration<ModuleBlobStorage, ModuleBlobStorageOption> registration)
        where TProvider : class, IBlobStorage
        => registration.ConfigureServices(context => context.Services.AddSingleton<IBlobStorage, TProvider>())
            .SatisfyFeature(ModuleBlobStorage.PROVIDER_FEATURE);
}

/// <summary>Owns the provider-independent blob storage contract.</summary>
public sealed class ModuleBlobStorage : MonicaModule<ModuleBlobStorageOption>
{
    /// <summary>The provider capability that an extension must satisfy after registering <see cref="IBlobStorage"/>.</summary>
    public const string PROVIDER_FEATURE = "blob-storage-provider";

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module) => module.RequireFeature(PROVIDER_FEATURE);

    /// <inheritdoc />
    public override void DeclareContracts(ModuleContractDescriptor<ModuleBlobStorageOption> contracts)
        => contracts.RequireService<IBlobStorage>();
}

/// <summary>Provider-independent blob storage module options; connection and naming options belong to the provider.</summary>
public sealed class ModuleBlobStorageOption : ModuleOptions<ModuleBlobStorage>;
