using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Core.Results;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationFacadeSourceAuthorityTests
{
    [Theory]
    [InlineData("valid", null)]
    [InlineData("missing-publication", "active published definition")]
    [InlineData("missing-document", "not persisted")]
    [InlineData("retired", "active published definition")]
    [InlineData("validation", "differs from the active publication")]
    [InlineData("section", "differs from the active publication")]
    [InlineData("document-schema", "stored document schema version")]
    [InlineData("unsupported", "cannot atomically guard")]
    [InlineData("unavailable", "could not be read")]
    public async Task SourceReads_WhenManualAuthorityIsMissingOrChanged_ShouldExposeTheSameReadOnlyReason(
        string scenario, string? expectedReason)
    {
        var local = TestConfigurationFactory.Definition() with
        {
            ValidationContract = new ConfigurationValidationContract
            {
                Capability = ConfigurationValidationCapability.PortableOnly, Revision = "portable-v1"
            }
        };
        local = WithCanonicalHash(local);
        var published = WithCanonicalHash(scenario switch
        {
            "validation" => local with { ValidationContract = local.ValidationContract with { Revision = "portable-v2" } },
            "section" => local with { SectionPath = "Changed:Section" },
            "document-schema" => local with { SchemaVersion = 2 },
            _ => local
        });
        var entry = CreateEntry(published, scenario == "retired"
            ? ConfigurationDefinitionLifecycleState.Retired : ConfigurationDefinitionLifecycleState.Active);
        entry.IsAvailable.Should().BeTrue();

        var metadata = Substitute.For<IConfigurationMetadataStore>();
        var values = Substitute.For<IConfigurationEffectiveValueStore>();
        var batches = Substitute.For<IConfigurationMutationBatchStore>();
        var descriptor = new ConfigurationStoreDescriptor { StoreKey = "test", DisplayName = "Test store" };
        metadata.Descriptor.Returns(descriptor);
        values.Descriptor.Returns(descriptor);
        values.SupportsPublishedDefinitionGuard.Returns(scenario != "unsupported");
        batches.SupportsPublishedDefinitionGuard.Returns(true);
        metadata.ListPublishedDefinitionEntriesAsync(Arg.Any<CancellationToken>())
            .Returns(scenario == "missing-publication" ? [] : new[] { entry });
        metadata.GetPublishedDefinitionEntryAsync(local.DefinitionKey, Arg.Any<CancellationToken>())
            .Returns(scenario == "unavailable"
                ? Task.FromException<ConfigurationPublishedDefinitionEntry?>(new IOException("synthetic-secret"))
                : Task.FromResult<ConfigurationPublishedDefinitionEntry?>(scenario == "missing-publication" ? null : entry));
        values.GetAsync(local.DefinitionKey, Arg.Any<CancellationToken>()).Returns(
            scenario == "missing-document" ? null : new ConfigurationEffectiveValueDocument
            {
                DefinitionKey = local.DefinitionKey, Json = "{}", Version = 1, SchemaVersion = 1
            });

        var source = new ConfigurationSourceDescriptor
        {
            SourceKey = "monica", ProviderType = "MonicaConfigurationProvider", DisplayName = "Monica Effective Store",
            Kind = ConfigurationSourceKind.MonicaEffectiveStore, IsWritable = true
        };
        var path = LogicalPath.FromProperties("WorkerId");
        var chain = new ConfigurationSourceChain
        {
            DefinitionKey = local.DefinitionKey, LogicalPath = path, ConfigurationPath = "Test:App:WorkerId",
            Values = [new ConfigurationSourceValue { Source = source, IsEffective = true, IsSensitive = true }]
        };
        var inspector = Substitute.For<IConfigurationSourceInspector>();
        inspector.GetSourceChain(Arg.Any<ConfigurationDefinition>(), path).Returns(chain);
        inspector.GetSourceChains(Arg.Any<ConfigurationDefinition>(), Arg.Any<IReadOnlyList<LogicalPath>>()).Returns([chain]);
        inspector.GetDefinitionContributions(Arg.Any<ConfigurationDefinition>()).Returns(
            [new ConfigurationDefinitionSourceContribution { Source = source, SuppliedValueCount = 1, EffectiveValueCount = 1 }]);
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(local);
        var context = new ConfigurationRuntimeContext { AutonomousWork = false };
        var registrations = new ServiceCollection();
        registrations.AddSingleton(new ConfigurationDefinitionResolver(registry, metadata));
        registrations.AddSingleton(inspector);
        registrations.AddSingleton(metadata);
        registrations.AddSingleton(values);
        registrations.AddSingleton(batches);
        registrations.AddSingleton(provider => new ConfigurationWriteAuthority(context, provider, new ConfigurationLocalDefinitionRegistry()));
        using var services = registrations.BuildServiceProvider();
        var facade = new ConfigurationFacade(services);

        var single = await facade.GetSourceChainAsync(local.DefinitionKey, path);
        var multiple = await facade.GetSourceChainsAsync(local.DefinitionKey, [path]);
        var contributions = await facade.GetDefinitionSourceContributionsAsync(local.DefinitionKey);

        single.Status.Should().Be(ResStatus.Ok);
        multiple.Status.Should().Be(ResStatus.Ok);
        contributions.Status.Should().Be(ResStatus.Ok);
        var projected = new[] { single.Data!.Values.Single().Source, multiple.Data!.Single().Values.Single().Source,
            contributions.Data!.Single().Source };
        foreach (var item in projected)
        {
            item.IsWritable.Should().Be(expectedReason is null);
            if (expectedReason is null) item.ReadOnlyReason.Should().BeNull();
            else item.ReadOnlyReason.Should().Contain(expectedReason).And.NotContain("synthetic-secret");
        }
        single.Data!.Values.Single().IsSensitive.Should().BeTrue();
        single.Data.Values.Single().DisplayValue.Should().BeNull();
        contributions.Data!.Single().EffectiveValueCount.Should().Be(1);
        source.IsWritable.Should().BeTrue("read-only projection must not mutate the inspector's source snapshot");
        values.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "SaveAsync" || call.GetMethodInfo().Name == "EnsureCreatedAsync");
        metadata.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name == "PublishAsync" || call.GetMethodInfo().Name == "RetirePublisherAsync");
    }

    private static ConfigurationDefinition WithCanonicalHash(ConfigurationDefinition definition) => definition with
    {
        SchemaHash = ConfigurationDefinitionSchemaCodec.ComputeSchemaHash(definition.DefinitionKey, definition.SectionPath, definition.Root)
    };

    private static ConfigurationPublishedDefinitionEntry CreateEntry(ConfigurationDefinition definition, ConfigurationDefinitionLifecycleState lifecycle)
        => ConfigurationPublishedDefinitionEntry.Materialize(new ConfigurationPublishedDefinitionRecord
        {
            StoreKey = "test", DefinitionKey = definition.DefinitionKey, SectionPath = definition.SectionPath,
            DisplayName = definition.DisplayName, ClrTypeName = definition.ClrTypeName, FromProject = definition.FromProject,
            SchemaVersion = definition.SchemaVersion, DefinitionRevision = 1, LifecycleState = lifecycle,
            SchemaHash = definition.SchemaHash, ReloadBehavior = definition.ReloadBehavior.ToString(),
            SchemaJson = ConfigurationDefinitionSchemaCodec.SerializeSchema(definition)
        });
}
