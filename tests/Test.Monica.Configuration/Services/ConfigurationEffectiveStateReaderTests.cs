using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationEffectiveStateReaderTests
{
    [Fact]
    public async Task ReadDefinitionsAsync_WhenDefinitionsArePublished_ShouldUseOneBulkReadAndPreserveStateOrder()
    {
        var firstDefinition = CreateDefinition("Definition.B", "Published:B", ConfigurationDefinitionOrigin.PublishedMetadata);
        var secondDefinition = CreateDefinition("Definition.A", "Published:A", ConfigurationDefinitionOrigin.PublishedMetadata);
        var firstDocument = CreateDocument(firstDefinition, "first", 17);
        var secondDocument = CreateDocument(secondDefinition, "second", 9);
        var store = CreateStore();
        store.GetManyAsync(
                Arg.Is<IReadOnlyList<string>>(keys => keys.SequenceEqual(new[] { "Definition.B", "Definition.A" })),
                TestContext.Current.CancellationToken)
            .Returns([firstDocument, secondDocument]);
        var sourceInspector = Substitute.For<IConfigurationSourceInspector>();
        var reader = CreateReader(store, sourceInspector, new ConfigurationBuilder().Build());

        var states = await reader.ReadDefinitionsAsync(
            [firstDefinition, secondDefinition],
            TestContext.Current.CancellationToken);

        states.Select(static state => state.Definition.DefinitionKey)
            .Should().Equal("Definition.B", "Definition.A");
        states.Select(static state => state.EffectiveValueVersion).Should().Equal(17, 9);
        AssertPublishedValues(states[0], "first", 17);
        AssertPublishedValues(states[1], "second", 9);
        await store.Received(1).GetManyAsync(
            Arg.Any<IReadOnlyList<string>>(),
            TestContext.Current.CancellationToken);
        await store.DidNotReceive().GetAsync(
            Arg.Any<string>(),
            TestContext.Current.CancellationToken);
        sourceInspector.DidNotReceiveWithAnyArgs().GetSourceChains(default!, default!);
    }

    [Fact]
    public async Task ReadDefinitionsAsync_WhenDefinitionIsLocal_ShouldBatchScalarSourcesAndRedactSensitiveAggregates()
    {
        var definition = CreateDefinition("Definition.Local", "Local", ConfigurationDefinitionOrigin.LocalScan);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Local:General:Name"] = "runtime-name",
                ["Local:Endpoints:0"] = "https://localhost:7001",
                ["Local:Map:primary"] = "runtime-map",
                ["Local:SecretGroup:Token"] = "runtime-secret",
                ["Local:CredentialsByName:primary:Token"] = "runtime-dictionary-secret",
                ["Local:Enabled"] = "true"
            })
            .Build();
        var store = CreateStore();
        store.GetManyAsync(Arg.Any<IReadOnlyList<string>>(), TestContext.Current.CancellationToken)
            .Returns([CreateDocument(definition, "unused", 41)]);
        var sourceInspector = Substitute.For<IConfigurationSourceInspector>();
        var externalSource = new ConfigurationSourceDescriptor
        {
            SourceKey = "memory",
            DisplayName = "Memory",
            ProviderType = "TestMemoryProvider",
            Kind = ConfigurationSourceKind.Memory
        };
        sourceInspector.GetSourceChains(
                definition,
                Arg.Any<IReadOnlyList<LogicalPath>>())
            .Returns(call => CreateSourceChains(
                definition,
                call.ArgAt<IReadOnlyList<LogicalPath>>(1),
                externalSource));
        var reader = CreateReader(store, sourceInspector, configuration);

        var state = (await reader.ReadDefinitionsAsync(
            [definition],
            TestContext.Current.CancellationToken)).Single();

        state.EffectiveValueVersion.Should().Be(41);
        GetValue(state, "General").DisplayValue.Should().Contain("runtime-name");
        GetValue(state, "General.Name").Should().BeEquivalentTo(new
        {
            DisplayValue = "source-name",
            IsSensitive = false,
            Version = (long?)null,
            EffectiveSource = externalSource
        });
        GetValue(state, "Endpoints").DisplayValue.Should().Contain("https://localhost:7001");
        GetValue(state, "Map").DisplayValue.Should().Contain("runtime-map");
        GetValue(state, "Enabled").Should().BeEquivalentTo(new
        {
            DisplayValue = "true",
            IsSensitive = false,
            Version = (long?)41
        });
        GetValue(state, "SecretGroup").Should().BeEquivalentTo(new
        {
            DisplayValue = (string?)null,
            IsSensitive = true
        });
        GetValue(state, "SecretGroup.Token").Should().BeEquivalentTo(new
        {
            DisplayValue = (string?)null,
            IsSensitive = true
        });
        GetValue(state, "CredentialsByName").Should().BeEquivalentTo(new
        {
            DisplayValue = (string?)null,
            IsSensitive = true
        });
        sourceInspector.Received(1).GetSourceChains(
            definition,
            Arg.Is<IReadOnlyList<LogicalPath>>(paths => paths.Select(static path => path.ToString())
                .SequenceEqual(new[] { "General.Name", "SecretGroup.Token", "Enabled" })));
        await store.Received(1).GetManyAsync(
            Arg.Any<IReadOnlyList<string>>(),
            TestContext.Current.CancellationToken);
        await store.DidNotReceive().GetAsync(
            Arg.Any<string>(),
            TestContext.Current.CancellationToken);
    }

    private static ConfigurationEffectiveStateReader CreateReader(
        IConfigurationEffectiveValueStore store,
        IConfigurationSourceInspector sourceInspector,
        IConfiguration configuration)
    {
        var runtimeContext = new ConfigurationRuntimeContext();
        runtimeContext.Capture(configuration);
        return new ConfigurationEffectiveStateReader(
            store,
            ConfigurationValidationTestServices.CreateSeedFactory(runtimeContext),
            new ConfigurationEffectiveValueDocumentEditor(
                new ConfigurationEffectiveValuePatchEngine(),
                new ConfigurationStoredValueCodec()),
            sourceInspector);
    }

    private static IConfigurationEffectiveValueStore CreateStore()
    {
        var store = Substitute.For<IConfigurationEffectiveValueStore>();
        store.Descriptor.Returns(new ConfigurationStoreDescriptor
        {
            StoreKey = "test-effective",
            DisplayName = "Test effective values",
            Kind = ConfigurationStoreKind.Database,
            SupportsEffectiveValues = true
        });
        return store;
    }

    private static IReadOnlyList<ConfigurationSourceChain> CreateSourceChains(
        ConfigurationDefinition definition,
        IReadOnlyList<LogicalPath> paths,
        ConfigurationSourceDescriptor source)
    {
        return paths.Select(path => new ConfigurationSourceChain
        {
            DefinitionKey = definition.DefinitionKey,
            LogicalPath = path,
            ConfigurationPath = $"{definition.SectionPath}:{string.Join(':', path.Segments.Select(static segment => segment.Value))}",
            Values = path.ToString() switch
            {
                "General.Name" =>
                [
                    new ConfigurationSourceValue
                    {
                        Source = source,
                        DisplayValue = "source-name",
                        IsEffective = true
                    }
                ],
                "SecretGroup.Token" =>
                [
                    new ConfigurationSourceValue
                    {
                        Source = source,
                        DisplayValue = "must-not-be-visible",
                        IsEffective = true
                    }
                ],
                _ => []
            }
        }).ToArray();
    }

    private static void AssertPublishedValues(
        ConfigurationDefinitionState state,
        string marker,
        long version)
    {
        state.EffectiveValues.Select(static value => value.LogicalPath.ToString()).Should().Equal(
            "General",
            "General.Name",
            "Endpoints",
            "Map",
            "SecretGroup",
            "SecretGroup.Token",
            "CredentialsByName",
            "Enabled");
        state.EffectiveValues.Should().AllSatisfy(value => value.Version.Should().Be(version));
        GetValue(state, "General").DisplayValue.Should().Contain(marker);
        GetValue(state, "General.Name").DisplayValue.Should().Be(marker);
        GetValue(state, "General.Name").EffectiveSource!.Kind.Should().Be(ConfigurationSourceKind.MonicaEffectiveStore);
        GetValue(state, "Endpoints").DisplayValue.Should().Contain($"https://{marker}.example");
        GetValue(state, "Map").DisplayValue.Should().Contain($"{marker}-map");
        GetValue(state, "Enabled").DisplayValue.Should().Be("true");
        GetValue(state, "SecretGroup").Should().BeEquivalentTo(new
        {
            DisplayValue = (string?)null,
            IsSensitive = true
        });
        GetValue(state, "SecretGroup.Token").Should().BeEquivalentTo(new
        {
            DisplayValue = (string?)null,
            IsSensitive = true
        });
        GetValue(state, "CredentialsByName").Should().BeEquivalentTo(new
        {
            DisplayValue = (string?)null,
            IsSensitive = true
        });
    }

    private static ConfigurationEffectiveValue GetValue(ConfigurationDefinitionState state, string path)
    {
        return state.EffectiveValues.Single(value => value.LogicalPath.ToString() == path);
    }

    private static ConfigurationEffectiveValueDocument CreateDocument(
        ConfigurationDefinition definition,
        string marker,
        long version)
    {
        var json = new JsonObject
        {
            ["General"] = new JsonObject { ["Name"] = marker },
            ["Endpoints"] = new JsonArray($"https://{marker}.example"),
            ["Map"] = new JsonObject { ["primary"] = $"{marker}-map" },
            ["SecretGroup"] = new JsonObject { ["Token"] = $"{marker}-secret" },
            ["CredentialsByName"] = new JsonObject
            {
                ["primary"] = new JsonObject { ["Token"] = $"{marker}-dictionary-secret" }
            },
            ["Enabled"] = true
        };
        return new ConfigurationEffectiveValueDocument
        {
            DefinitionKey = definition.DefinitionKey,
            Json = json.ToJsonString(),
            Version = version,
            SchemaVersion = definition.SchemaVersion
        };
    }

    private static ConfigurationDefinition CreateDefinition(
        string definitionKey,
        string sectionPath,
        ConfigurationDefinitionOrigin origin)
    {
        var generalPath = LogicalPath.FromProperties("General");
        var secretGroupPath = LogicalPath.FromProperties("SecretGroup");
        var sensitiveScalarTemplate = ScalarNode(
            "Token",
            new LogicalPath([new DictionaryKeySegment("*"), new PropertySegment("Token")]),
            $"{sectionPath}:CredentialsByName:*:Token",
            isSensitive: true);
        return new ConfigurationDefinition
        {
            DefinitionKey = definitionKey,
            SectionPath = sectionPath,
            DisplayName = definitionKey,
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            FromProject = "Test.Project",
            SchemaHash = $"hash:{definitionKey}",
            Origin = origin,
            Root = new ConfigurationNodeDefinition
            {
                NodeKey = string.Empty,
                Name = "Options",
                RelativePath = LogicalPath.Root,
                ConfigurationPath = sectionPath,
                ClrTypeName = typeof(object).AssemblyQualifiedName!,
                NodeKind = ConfigurationNodeKind.Object,
                Children =
                [
                    ObjectNode(
                        "General",
                        generalPath,
                        $"{sectionPath}:General",
                        [ScalarNode("Name", generalPath.Append(new PropertySegment("Name")), $"{sectionPath}:General:Name")]),
                    ListNode("Endpoints", sectionPath),
                    DictionaryNode("Map", sectionPath, ScalarNode("Value", LogicalPath.Root, string.Empty)),
                    ObjectNode(
                        "SecretGroup",
                        secretGroupPath,
                        $"{sectionPath}:SecretGroup",
                        [ScalarNode("Token", secretGroupPath.Append(new PropertySegment("Token")), $"{sectionPath}:SecretGroup:Token", true)]),
                    DictionaryNode(
                        "CredentialsByName",
                        sectionPath,
                        ObjectNode("Value", LogicalPath.Root, string.Empty, [sensitiveScalarTemplate])),
                    ScalarNode("Enabled", LogicalPath.FromProperties("Enabled"), $"{sectionPath}:Enabled", valueKind: ConfigurationValueKind.Boolean)
                ]
            }
        };
    }

    private static ConfigurationNodeDefinition ScalarNode(
        string name,
        LogicalPath path,
        string configurationPath,
        bool isSensitive = false,
        ConfigurationValueKind valueKind = ConfigurationValueKind.String)
    {
        return new ConfigurationNodeDefinition
        {
            NodeKey = path.ToString(),
            Name = name,
            RelativePath = path,
            ConfigurationPath = configurationPath,
            ClrTypeName = typeof(string).AssemblyQualifiedName!,
            NodeKind = ConfigurationNodeKind.Scalar,
            ValueKind = valueKind,
            IsSensitive = isSensitive
        };
    }

    private static ConfigurationNodeDefinition ObjectNode(
        string name,
        LogicalPath path,
        string configurationPath,
        IReadOnlyList<ConfigurationNodeDefinition> children)
    {
        return new ConfigurationNodeDefinition
        {
            NodeKey = path.ToString(),
            Name = name,
            RelativePath = path,
            ConfigurationPath = configurationPath,
            ClrTypeName = typeof(object).AssemblyQualifiedName!,
            NodeKind = ConfigurationNodeKind.Object,
            Children = children
        };
    }

    private static ConfigurationNodeDefinition ListNode(string name, string sectionPath)
    {
        var path = LogicalPath.FromProperties(name);
        return new ConfigurationNodeDefinition
        {
            NodeKey = path.ToString(),
            Name = name,
            RelativePath = path,
            ConfigurationPath = $"{sectionPath}:{name}",
            ClrTypeName = typeof(List<string>).AssemblyQualifiedName!,
            NodeKind = ConfigurationNodeKind.List,
            ListTemplate = new ConfigurationListTemplate
            {
                ItemTemplate = ScalarNode("Item", LogicalPath.Root, string.Empty)
            }
        };
    }

    private static ConfigurationNodeDefinition DictionaryNode(
        string name,
        string sectionPath,
        ConfigurationNodeDefinition valueTemplate)
    {
        var path = LogicalPath.FromProperties(name);
        return new ConfigurationNodeDefinition
        {
            NodeKey = path.ToString(),
            Name = name,
            RelativePath = path,
            ConfigurationPath = $"{sectionPath}:{name}",
            ClrTypeName = typeof(Dictionary<string, string>).AssemblyQualifiedName!,
            NodeKind = ConfigurationNodeKind.Dictionary,
            DictionaryTemplate = new ConfigurationDictionaryTemplate
            {
                KeyClrTypeName = typeof(string).AssemblyQualifiedName!,
                KeyKind = ConfigurationValueKind.String,
                ValueTemplate = valueTemplate
            }
        };
    }
}
