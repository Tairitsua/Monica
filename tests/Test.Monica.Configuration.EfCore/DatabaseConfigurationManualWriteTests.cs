using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Xunit;

namespace Test.Monica.Configuration.EfCore;

public sealed partial class DatabaseConfigurationStorePublishTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedWrite_WhenDocumentHasOlderSchema_ShouldRejectWithoutMigratingIt(bool useBatch)
    {
        var token = TestContext.Current.CancellationToken;
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var metadata = provider.GetRequiredService<IConfigurationMetadataStore>();
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var definition = ManualDefinition("Test.Manual.OlderDocument") with { SchemaVersion = 2 };
        await metadata.PublishAsync(CreatePublication([definition]), token);
        var reviewed = (await metadata.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, token))!.RequireDefinition();
        reviewed.SchemaVersion.Should().Be(2);
        await values.EnsureCreatedAsync(definition with { SchemaVersion = 1 }, """{"Enabled":false}""", token);
        var initial = (await values.GetAsync(definition.DefinitionKey, token))!;
        var request = CreatePairedBatch(reviewed, initial.Version);
        var save = request.Items[0].SaveRequest with
        {
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = reviewed }
        };
        request = request with { Items = [request.Items[0] with { SaveRequest = save }] };
        Func<Task> write = useBatch
            ? () => provider.GetRequiredService<IConfigurationMutationBatchStore>().CommitAsync(request, token)
            : () => values.SaveAsync(save, token);

        await write.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        (await values.GetAsync(definition.DefinitionKey, token)).Should().BeEquivalentTo(initial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedSave_WhenPublicationWasRetiredOrChanged_ShouldRejectInsideTheStore(bool retire)
    {
        var token = TestContext.Current.CancellationToken;
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var metadata = provider.GetRequiredService<IConfigurationMetadataStore>();
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var definition = ManualDefinition("Test.Manual.GuardedSave");
        var publication = CreatePublication([definition]);
        await metadata.PublishAsync(publication, token);
        var reviewed = (await metadata.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, token))!.RequireDefinition();
        var initial = await values.EnsureCreatedAsync(definition, """{"Enabled":false}""", token);
        initial = (await values.GetAsync(definition.DefinitionKey, token))!;
        if (retire) await metadata.RetirePublisherAsync(publication.Publisher, token);
        else await metadata.PublishAsync(CreatePublication([definition with { DisplayName = "Changed after review" }]), token);

        var save = () => values.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = reviewed, Json = """{"Enabled":true}""", ExpectedVersion = initial.Version,
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = reviewed }
        }, token);

        await save.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        (await values.GetAsync(definition.DefinitionKey, token)).Should().BeEquivalentTo(initial);
    }

    [Fact]
    public async Task GuardedBatch_WhenLaterAuthorityChanges_ShouldRollbackEveryValueAndAuditRow()
    {
        var token = TestContext.Current.CancellationToken;
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var metadata = provider.GetRequiredService<IConfigurationMetadataStore>();
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var histories = provider.GetRequiredService<IConfigurationHistoryStore>();
        var batch = provider.GetRequiredService<IConfigurationMutationBatchStore>();
        var first = ManualDefinition("Test.Manual.First");
        var second = ManualDefinition("Test.Manual.Second");
        await metadata.PublishAsync(CreatePublication([first, second]), token);
        first = (await metadata.GetPublishedDefinitionEntryAsync(first.DefinitionKey, token))!.RequireDefinition();
        second = (await metadata.GetPublishedDefinitionEntryAsync(second.DefinitionKey, token))!.RequireDefinition();
        var initialFirst = await values.EnsureCreatedAsync(first, """{"Enabled":false}""", token);
        var initialSecond = await values.EnsureCreatedAsync(second, """{"Enabled":false}""", token);
        initialFirst = (await values.GetAsync(first.DefinitionKey, token))!;
        initialSecond = (await values.GetAsync(second.DefinitionKey, token))!;
        var request = CreatePairedBatch(first, initialFirst.Version);
        var other = CreatePairedBatch(second, initialSecond.Version).Items[0];
        request = request with { Items = [request.Items[0] with
        {
            SaveRequest = request.Items[0].SaveRequest with
            {
                PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = first }
            }
        }, other with
        {
            SaveRequest = other.SaveRequest with
            {
                PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = second }
            }
        }] };
        await metadata.PublishAsync(CreatePublication([first, second with
        {
            ValidationContract = second.ValidationContract with { Revision = "portable-v2" }
        }]), token);

        var commit = () => batch.CommitAsync(request, token);

        await commit.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        (await values.GetAsync(first.DefinitionKey, token)).Should().BeEquivalentTo(initialFirst);
        (await values.GetAsync(second.DefinitionKey, token)).Should().BeEquivalentTo(initialSecond);
        (await histories.QueryHistoryAsync(null, null, null, null, request.MutationGroup.GroupId, token)).Should().BeEmpty();
        (await histories.GetGroupAsync(request.MutationGroup.GroupId, token)).Should().BeNull();
    }

    [Fact]
    public async Task GuardedSave_WhenExistingAuthorityMatches_ShouldCommit()
    {
        var token = TestContext.Current.CancellationToken;
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var metadata = provider.GetRequiredService<IConfigurationMetadataStore>();
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var definition = ManualDefinition("Test.Manual.Allowed");
        await metadata.PublishAsync(CreatePublication([definition]), token);
        var reviewed = (await metadata.GetPublishedDefinitionEntryAsync(definition.DefinitionKey, token))!.RequireDefinition();
        var initial = await values.EnsureCreatedAsync(definition, """{"Enabled":false}""", token);

        var saved = await values.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = reviewed, Json = """{"Enabled":true}""", ExpectedVersion = initial.Version,
            PublishedDefinitionExpectation = new ConfigurationPublishedDefinitionExpectation { Definition = reviewed }
        }, token);

        saved.Version.Should().Be(initial.Version + 1);
        saved.Json.Should().Contain("true");
    }

    private static ConfigurationDefinition ManualDefinition(string key) => CreateDefinition(key) with
    {
        ValidationContract = new ConfigurationValidationContract
        {
            Capability = ConfigurationValidationCapability.PortableOnly, Revision = "portable-v1"
        }
    };
}
