using AwesomeAssertions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Services;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationDefinitionLifecycleTests
{
    [Fact]
    public async Task Resolver_WhenStartupSharesLegacyManagedKey_ShouldIgnoreItsMetadataAndKeepRemoteManagedCatalog()
    {
        var startup = CreatePersistableDefinition("Test.Startup.Ownership") with { Kind = ConfigurationKind.Startup };
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(startup);
        var store = Substitute.For<IConfigurationMetadataStore>();
        var remote = CreatePersistableDefinition("Test.Remote.Managed");
        store.ListPublishedDefinitionEntriesAsync(Arg.Any<CancellationToken>()).Returns(
            [CreateEntry(startup with { Kind = ConfigurationKind.Managed, SchemaVersion = 31 }, ConfigurationDefinitionLifecycleState.Retired),
                CreateEntry(remote, ConfigurationDefinitionLifecycleState.Active)]);
        var resolver = new ConfigurationDefinitionResolver(registry, store);

        (await resolver.GetRequiredAsync(startup.DefinitionKey, CancellationToken.None)).Should().BeSameAs(startup);
        await store.DidNotReceiveWithAnyArgs().GetPublishedDefinitionEntryAsync(startup.DefinitionKey, TestContext.Current.CancellationToken);
        var definitions = await resolver.GetMergedDefinitionsAsync(CancellationToken.None);
        definitions.Single(definition => definition.Kind == ConfigurationKind.Startup).SchemaVersion.Should().Be(startup.SchemaVersion);
        definitions.Should().Contain(definition => definition.DefinitionKey == remote.DefinitionKey);
        var catalog = await resolver.GetDiagnosticCatalogAsync(CancellationToken.None);
        catalog.Entries.Single(entry => entry.Definition?.Kind == ConfigurationKind.Startup).Availability.Should().Be(ConfigurationDefinitionAvailability.Available);
        catalog.Entries.Single(entry => entry.Definition?.Kind == ConfigurationKind.Startup).PublishedMetadata.Should().BeNull();

        registry.Register(CreatePersistableDefinition("Test.Managed.Ownership"));
        var mixed = await resolver.GetMergedDefinitionsAsync(CancellationToken.None);
        mixed.Single(definition => definition.DefinitionKey == startup.DefinitionKey).SchemaVersion.Should().Be(startup.SchemaVersion);
        (await resolver.GetDiagnosticCatalogAsync(CancellationToken.None)).Entries
            .Single(entry => entry.Definition?.Kind == ConfigurationKind.Startup).PublishedMetadata.Should().BeNull();
    }

    [Fact]
    public async Task GetDiagnosticCatalog_WhenConfiguredStoreCannotOpen_ShouldKeepStartupAvailableWithoutDisclosingStoreFailure()
    {
        var startup = CreatePersistableDefinition("Test.Startup.UnavailableStore") with { Kind = ConfigurationKind.Startup };
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(startup);
        var resolver = new ConfigurationDefinitionResolver(registry,
            () => throw new InvalidOperationException("synthetic-connection-secret"));

        var catalog = await resolver.GetDiagnosticCatalogAsync(TestContext.Current.CancellationToken);

        catalog.Entries.Single().Availability.Should().Be(ConfigurationDefinitionAvailability.Available);
        catalog.Entries.Single().Definition.Should().BeEquivalentTo(startup);
        catalog.StoreDiagnostic.Should().NotBeNull();
        catalog.StoreDiagnostic!.Message.Should().NotContain("synthetic-connection-secret");
    }

    [Fact]
    public async Task Resolver_WhenPublishedDefinitionIsRetired_ShouldHideItOperationallyButKeepItDiagnostic()
    {
        var active = CreatePersistableDefinition("Test.Lifecycle.Active");
        var retired = CreatePersistableDefinition("Test.Lifecycle.Retired");
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.ListPublishedDefinitionEntriesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                CreateEntry(active, ConfigurationDefinitionLifecycleState.Active),
                CreateEntry(retired, ConfigurationDefinitionLifecycleState.Retired)
            ]);
        var resolver = new ConfigurationDefinitionResolver(new ConfigurationDefinitionRegistry(), metadataStore);

        var operational = await resolver.GetMergedDefinitionsAsync(TestContext.Current.CancellationToken);
        var diagnostic = await resolver.GetDiagnosticCatalogAsync(TestContext.Current.CancellationToken);
        var authoritativeRead = () => resolver.GetRequiredAsync(
            retired.DefinitionKey,
            TestContext.Current.CancellationToken);
        var diagnosticRead = await resolver.GetRequiredForReadAsync(
            retired.DefinitionKey,
            TestContext.Current.CancellationToken);

        operational.Select(static definition => definition.DefinitionKey)
            .Should().Equal(active.DefinitionKey);
        await authoritativeRead.Should().ThrowAsync<ConfigurationDefinitionNotFoundException>();
        diagnosticRead.DefinitionKey.Should().Be(retired.DefinitionKey);
        diagnostic.Entries.Select(static entry => entry.Definition!.DefinitionKey)
            .Should().BeEquivalentTo([active.DefinitionKey, retired.DefinitionKey]);
        diagnostic.Entries.Single(entry => entry.Definition!.DefinitionKey == retired.DefinitionKey)
            .LifecycleState.Should().Be(ConfigurationDefinitionLifecycleState.Retired);
    }

    [Fact]
    public async Task Resolver_WhenRetiredMetadataMatchesLocalDefinition_ShouldKeepPersistedVersionsButUseLocalReloadEvidence()
    {
        var local = CreatePersistableDefinition("Test.Lifecycle.Reactivated") with
        {
            ReloadBehavior = ConfigurationReloadBehavior.OnlineReloadable
        };
        var published = local with
        {
            SchemaVersion = 7,
            DefinitionRevision = 11,
            ReloadBehavior = ConfigurationReloadBehavior.StaticAfterStartup
        };
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(local);
        var metadataStore = Substitute.For<IConfigurationMetadataStore>();
        metadataStore.ListPublishedDefinitionEntriesAsync(Arg.Any<CancellationToken>())
            .Returns([CreateEntry(published, ConfigurationDefinitionLifecycleState.Retired)]);
        metadataStore.GetPublishedDefinitionEntryAsync(local.DefinitionKey, Arg.Any<CancellationToken>())
            .Returns(CreateEntry(published, ConfigurationDefinitionLifecycleState.Retired));
        var resolver = new ConfigurationDefinitionResolver(registry, metadataStore);

        var operational = (await resolver.GetMergedDefinitionsAsync(TestContext.Current.CancellationToken)).Single();
        var required = await resolver.GetRequiredAsync(local.DefinitionKey, TestContext.Current.CancellationToken);
        var diagnostic = (await resolver.GetDiagnosticCatalogAsync(TestContext.Current.CancellationToken)).Entries.Single();

        operational.SchemaVersion.Should().Be(7);
        operational.DefinitionRevision.Should().Be(11);
        operational.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.OnlineReloadable);
        required.Should().BeEquivalentTo(operational);
        diagnostic.LifecycleState.Should().Be(ConfigurationDefinitionLifecycleState.Active);
        diagnostic.PublishedMetadata!.LifecycleState.Should().Be(ConfigurationDefinitionLifecycleState.Retired);
        diagnostic.Definition!.ReloadBehavior.Should().Be(ConfigurationReloadBehavior.OnlineReloadable);
    }

    [Fact]
    public async Task PurgeAsync_WhenDefinitionIsLocallyRegistered_ShouldRejectBeforeStoreMutation()
    {
        var definition = TestConfigurationFactory.Definition();
        var registry = new ConfigurationDefinitionRegistry();
        registry.Register(definition);
        var maintenanceStore = Substitute.For<IConfigurationDefinitionMaintenanceStore>();
        maintenanceStore.PreviewDefinitionPurgeAsync(definition.DefinitionKey, Arg.Any<CancellationToken>())
            .Returns(CreateRetiredPreview(definition));
        var service = new ConfigurationDefinitionLifecycleService(
            registry,
            maintenanceStore,
            new ConfigurationDefinitionResolver(registry));

        var act = () => service.PurgeAsync(
            new ConfigurationDefinitionPurgeRequest
            {
                DefinitionKey = definition.DefinitionKey,
                ExpectedDefinitionRevision = 3
            },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConfigurationConcurrencyConflictException>()
            .WithMessage("*registered by the current process*");
        await maintenanceStore.DidNotReceive().PurgeDefinitionAsync(
            Arg.Any<ConfigurationDefinitionPurgeRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static ConfigurationDefinition CreatePersistableDefinition(string definitionKey)
    {
        var definition = TestConfigurationFactory.Definition() with
        {
            DefinitionKey = definitionKey,
            SectionPath = definitionKey.Replace('.', ':'),
            DisplayName = definitionKey
        };
        return definition with
        {
            SchemaHash = ConfigurationDefinitionSchemaCodec.ComputeSchemaHash(
                definition.DefinitionKey,
                definition.SectionPath,
                definition.Root)
        };
    }

    private static ConfigurationPublishedDefinitionEntry CreateEntry(
        ConfigurationDefinition definition,
        ConfigurationDefinitionLifecycleState lifecycleState)
    {
        return ConfigurationPublishedDefinitionEntry.Materialize(new ConfigurationPublishedDefinitionRecord
        {
            StoreKey = "test",
            DefinitionKey = definition.DefinitionKey,
            SectionPath = definition.SectionPath,
            DisplayName = definition.DisplayName,
            ClrTypeName = definition.ClrTypeName,
            FromProject = definition.FromProject,
            Category = definition.Category,
            SchemaVersion = definition.SchemaVersion,
            DefinitionRevision = Math.Max(definition.DefinitionRevision, 1),
            LifecycleState = lifecycleState,
            SchemaHash = definition.SchemaHash,
            ReloadBehavior = definition.ReloadBehavior.ToString(),
            SchemaJson = ConfigurationDefinitionSchemaCodec.SerializeSchema(definition)
        });
    }

    private static ConfigurationDefinitionPurgePreview CreateRetiredPreview(ConfigurationDefinition definition)
    {
        return new ConfigurationDefinitionPurgePreview
        {
            DefinitionKey = definition.DefinitionKey,
            DisplayName = definition.DisplayName,
            LifecycleState = ConfigurationDefinitionLifecycleState.Retired,
            DefinitionRevision = 3
        };
    }
}
