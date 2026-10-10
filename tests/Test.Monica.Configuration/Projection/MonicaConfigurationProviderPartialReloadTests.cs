using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Configuration.Stores.File;
using Xunit;

namespace Test.Monica.Configuration.Projection;

public sealed class MonicaConfigurationProviderPartialReloadTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"monica-config-provider-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReloadDefinitionAsync_WhenOneDefinitionChanges_ShouldUpdateOnlyThatProjection()
    {
        var definitionA = CreateDefinition("Test.DefinitionA", "Test:A");
        var definitionB = CreateDefinition("Test.DefinitionB", "Test:B");
        var store = CreateStore();
        await store.EnsureCreatedAsync(definitionA, """{"Value":"a1","Extra":"gone"}""", CancellationToken.None);
        await store.EnsureCreatedAsync(definitionB, """{"Value":"b1","Extra":"keep"}""", CancellationToken.None);
        var provider = CreateProvider(store, definitionA, definitionB);
        await provider.ReloadAsync(CancellationToken.None);
        await provider.ReloadDefinitionAsync(definitionA.DefinitionKey, minimumVersion: 1, CancellationToken.None);
        provider.SuccessfulProjectionRevision.Should().Be(1);
        await store.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = definitionA,
            Json = """{"Value":"a2"}""",
            ExpectedVersion = 1
        }, CancellationToken.None);

        await provider.ReloadDefinitionAsync(definitionA.DefinitionKey, minimumVersion: 2, CancellationToken.None);

        provider.TryGet("Test:A:Value", out var changedValue).Should().BeTrue();
        changedValue.Should().Be("a2");
        provider.TryGet("Test:A:Extra", out _).Should().BeFalse();
        provider.TryGet("Test:B:Value", out var unchangedValue).Should().BeTrue();
        unchangedValue.Should().Be("b1");
        provider.GetLoadedVersion(definitionA.DefinitionKey).Should().Be(2);
        provider.GetLoadedVersion(definitionB.DefinitionKey).Should().Be(1);
        provider.SuccessfulProjectionRevision.Should().Be(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private MonicaConfigurationProvider CreateProvider(
        IConfigurationEffectiveValueStore store,
        params ConfigurationDefinition[] definitions)
    {
        var registry = new ConfigurationDefinitionRegistry();
        foreach (var definition in definitions)
        {
            registry.Register(definition);
        }

        var configuration = new ConfigurationBuilder().Build();
        var runtimeContext = new ConfigurationRuntimeContext();
        runtimeContext.Capture(configuration);

        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton(runtimeContext);
        services.AddSingleton<IConfigurationDefinitionRegistry>(registry);
        services.AddSingleton(store);
        services.AddSingleton<IConfigurationEffectiveValueStore>(store);
        ConfigurationValidationTestServices.AddCoreServices(services);
        services.AddSingleton<ConfigurationStoredValueCodec>();
        services.AddSingleton<ConfigurationEffectiveValuePatchEngine>();
        services.AddSingleton<ConfigurationEffectiveValueDocumentEditor>();
        services.AddSingleton<IConfigurationStoreStateTracker, ConfigurationStoreStateTracker>();
        services.AddSingleton<ConfigurationMetricsRecorder>();

        var accessor = new MonicaConfigurationProviderAccessor();
        var provider = new MonicaConfigurationProvider(accessor);
        accessor.Provider = provider;
        accessor.ServiceProvider = services.BuildServiceProvider();
        return provider;
    }

    private FileConfigurationStore CreateStore()
    {
        return new FileConfigurationStore(Options.Create(new ConfigurationFileStoreOptions
        {
            RootDirectory = _rootDirectory
        }));
    }

    private static ConfigurationDefinition CreateDefinition(string definitionKey, string sectionPath)
    {
        return new ConfigurationDefinition
        {
            DefinitionKey = definitionKey,
            SectionPath = sectionPath,
            DisplayName = definitionKey,
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            FromProject = "Test",
            SchemaHash = $"sha256:{definitionKey}",
            Root = new ConfigurationNodeDefinition
            {
                NodeKey = string.Empty,
                Name = "Root",
                RelativePath = LogicalPath.Root,
                ConfigurationPath = sectionPath,
                ClrTypeName = typeof(object).AssemblyQualifiedName!,
                NodeKind = ConfigurationNodeKind.Object,
                Children =
                [
                    TestConfigurationFactory.ScalarNode("Value", typeof(string), ConfigurationValueKind.String, configurationPath: $"{sectionPath}:Value"),
                    TestConfigurationFactory.ScalarNode("Extra", typeof(string), ConfigurationValueKind.String, configurationPath: $"{sectionPath}:Extra")
                ]
            }
        };
    }
}
