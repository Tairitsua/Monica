using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Monica.AI.Configuration.Providers;
using Monica.AI.Configuration.Services;
using Monica.AI.Providers.Anthropic;
using Monica.AI.Providers.OpenAI;
using Monica.AI.Services;
using Monica.AI.Storage.Providers;
using Monica.Modules;

namespace Test.Monica.AI.Support;

internal sealed class ConfigurationTestWorkspace : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("monica-ai-configuration-");
    public string RootPath => _directory.FullName;
    public AIModelCatalog Catalog { get; } = new();
    public AIFileStore Files { get; }
    public FileAIConfigurationStore Store { get; }

    public ConfigurationTestWorkspace()
    {
        Catalog.AddReservedModels(OpenAIReservedModels.Models);
        Catalog.AddReservedModels(AnthropicReservedModels.Models);
        Files = new AIFileStore(Options.Create(new ModuleAIOption { StorageRootPath = RootPath }));
        Store = new FileAIConfigurationStore(Files);
    }

    public AIConfigurationService CreateService(params AIProviderDefinition[] definitions) =>
        CreateService(new ModuleAIOption(), definitions);

    public AIConfigurationService CreateService(ModuleAIOption options, params AIProviderDefinition[] definitions) =>
        new(Store, definitions, Catalog, DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(RootPath, "test-key-ring"))),
            Options.Create(options));

    public void Dispose() => _directory.Delete(recursive: true);
}
