using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Stores.File;
using Xunit;

namespace Test.Monica.Configuration.Stores.File;

public sealed class FileConfigurationManualWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"monica-manual-store-{Guid.NewGuid():N}");
    private static readonly ConfigurationPublisherIdentity Publisher = new()
    {
        PublisherKey = "manual-test-owner", InstanceId = "manual-test-owner:1", Name = "Manual test owner"
    };

    [Fact]
    public async Task NativeWritersAcrossInstances_ShouldWaitForSharedLeaseAndRecheckPublicationAfterCancellation()
    {
        var token = TestContext.Current.CancellationToken;
        var owner = CreateStore();
        var manual = CreateStore();
        var definition = Definition();
        await owner.PublishAsync(ConfigurationDefinitionPublicationBatch.Create(Publisher, [definition]), token);
        var reviewed = (await owner.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, token))!.RequireDefinition();
        var initial = await owner.EnsureCreatedAsync(definition, """{"WorkerId":1}""", token);
        var request = new ConfigurationEffectiveValueSaveRequest
        {
            Definition = reviewed, Json = """{"WorkerId":2}""", ExpectedVersion = initial.Version,
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = reviewed }
        };
        Task publication;
        await using (var lease = await ConfigurationFileWriteLease.AcquireAsync(_root, true, token))
        {
            publication = owner.PublishAsync(ConfigurationDefinitionPublicationBatch.Create(Publisher,
                [definition with { DisplayName = "Changed while the editor waits" }]), token);
            publication.IsCompleted.Should().BeFalse();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var save = manual.SaveAsync(request, cancellation.Token);
            save.IsCompleted.Should().BeFalse();
            await cancellation.CancelAsync();
            var cancelled = () => save;
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
            (await manual.GetAsync(definition.DefinitionKey, token)).Should().BeEquivalentTo(initial);
        }
        await publication.WaitAsync(TimeSpan.FromSeconds(5), token);

        var retry = () => manual.SaveAsync(request, token);
        await retry.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        (await manual.GetAsync(definition.DefinitionKey, token)).Should().BeEquivalentTo(initial);
        System.IO.File.Exists(Path.Combine(_root, ConfigurationFileWriteLease.FileName)).Should().BeTrue();
    }

    [Fact]
    public async Task GuardedSave_WhenDirectoryIsMissing_ShouldNotCreateALeaseOrStore()
    {
        var definition = Definition();
        var save = () => CreateStore().SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = definition, Json = "{}",
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = definition }
        }, TestContext.Current.CancellationToken);

        await save.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task VersionReads_ShouldRetainInterruptedDeletionUntilAnExplicitWrite()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(_root, "history", "unified-versions");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "v99.deleting");
        var orphan = Path.Combine(directory, "v99.json");
        await System.IO.File.WriteAllTextAsync(Path.Combine(directory, "index.json"), "[]", token);
        await System.IO.File.WriteAllTextAsync(marker, string.Empty, token);
        await System.IO.File.WriteAllTextAsync(orphan, "{}", token);
        var store = CreateStore();

        (await store.ListVersionsAsync(null, null, null, 10, token)).Should().BeEmpty();
        System.IO.File.Exists(marker).Should().BeTrue();
        System.IO.File.Exists(orphan).Should().BeTrue();
        await store.AppendVersionAsync(new ConfigurationUnifiedVersionCreateRequest(), token);
        System.IO.File.Exists(marker).Should().BeFalse();
        System.IO.File.Exists(orphan).Should().BeFalse();
    }

    [Fact]
    public async Task MetadataAndVersionReads_WhenStoreIsAbsent_ShouldNotCreateDirectories()
    {
        var store = CreateStore();
        var token = TestContext.Current.CancellationToken;
        (await store.ListPublishedDefinitionEntriesAsync(token)).Should().BeEmpty();
        (await store.GetPublishedDefinitionEntryAsync("missing", token)).Should().BeNull();
        (await store.GetDefinitionPublisherStatesAsync(["missing"], token))["missing"].Should().BeEmpty();
        (await store.ListVersionsAsync(null, null, null, 10, token)).Should().BeEmpty();
        var overview = () => store.GetDefinitionPublicationOverviewAsync("missing", 10, token);
        await overview.Should().ThrowAsync<KeyNotFoundException>();
        var purge = () => store.PreviewDefinitionPurgeAsync("missing", token);
        await purge.Should().ThrowAsync<KeyNotFoundException>();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Theory]
    [InlineData("missing-publication")]
    [InlineData("missing-document")]
    [InlineData("retired")]
    [InlineData("revision")]
    [InlineData("section")]
    [InlineData("schema")]
    [InlineData("validation")]
    [InlineData("document-schema")]
    public async Task SaveAsync_WhenPublishedAuthorityChanges_ShouldRejectWithoutChangingTheDocument(string drift)
    {
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var definition = Definition();
        if (drift == "document-schema") definition = definition with { SchemaVersion = 2 };
        if (drift != "missing-publication")
            await store.PublishAsync(ConfigurationDefinitionPublicationBatch.Create(Publisher, [definition]), token);
        var published = await store.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, token);
        var reviewed = published?.RequireDefinition() ?? definition;
        ConfigurationEffectiveValueDocument? initial = null;
        if (drift != "missing-document") initial = await store.EnsureCreatedAsync(
            drift == "document-schema" ? definition with { SchemaVersion = 1 } : definition, """{"WorkerId":1}""", token);
        if (drift == "retired") await store.RetirePublisherAsync(Publisher, token);
        if (drift is "revision" or "section" or "schema" or "validation")
        {
            var replacement = drift switch
            {
                "section" => definition with { SectionPath = "Test:Moved" },
                "schema" => definition with { SchemaHash = "sha256:replacement" },
                "validation" => definition with { ValidationContract = definition.ValidationContract with { Revision = "portable-v2" } },
                _ => definition with { DisplayName = "New metadata revision" }
            };
            await store.PublishAsync(ConfigurationDefinitionPublicationBatch.Create(Publisher, [replacement]), token);
        }

        var save = () => store.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = reviewed, Json = """{"WorkerId":2}""", ExpectedVersion = initial?.Version,
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = reviewed }
        }, token);

        await save.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        (await store.GetAsync(definition.DefinitionKey, token)).Should().BeEquivalentTo(initial);
    }

    [Fact]
    public async Task SaveAsync_WhenAuthorityAndDocumentMatch_ShouldPersistOneNewVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var definition = Definition();
        await store.PublishAsync(ConfigurationDefinitionPublicationBatch.Create(Publisher, [definition]), token);
        var reviewed = (await store.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, token))!.RequireDefinition();
        var initial = await store.EnsureCreatedAsync(definition, """{"WorkerId":1}""", token);

        var saved = await store.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = reviewed, Json = """{"WorkerId":2}""", ExpectedVersion = initial.Version,
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = reviewed }
        }, token);

        saved.Version.Should().Be(initial.Version + 1);
        saved.Json.Should().Contain("2");
    }

    private FileConfigurationStore CreateStore() => new(Options.Create(new ConfigurationFileStoreOptions { RootDirectory = _root }));

    private static ConfigurationDefinition Definition()
    {
        var definition = TestConfigurationFactory.Definition();
        return definition with
        {
            SchemaHash = ConfigurationDefinitionSchemaCodec.ComputeSchemaHash(definition.DefinitionKey, definition.SectionPath, definition.Root),
            ValidationContract = new ConfigurationValidationContract
            {
                Capability = ConfigurationValidationCapability.PortableOnly, Revision = "portable-v1"
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
