using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Xunit;

namespace Test.Monica.Configuration.EfCore;

public sealed partial class DatabaseConfigurationStorePublishTests
{
    [Fact]
    public async Task CommitAsync_WhenTwoCommandsShareDefinition_ShouldPersistOneVersionAndOrderedStageHistories()
    {
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var histories = provider.GetRequiredService<IConfigurationHistoryStore>();
        var batchStore = provider.GetRequiredService<IConfigurationMutationBatchStore>();
        var definition = CreateDefinition("Test.Batch.Paired");
        var initial = await values.EnsureCreatedAsync(definition, "{\"Enabled\":false}", TestContext.Current.CancellationToken);
        var request = CreatePairedBatch(definition, initial.Version);

        var result = await batchStore.CommitAsync(request, TestContext.Current.CancellationToken);

        result.AppliedRequestIds.Should().Equal("enable", "disable");
        result.Documents[definition.DefinitionKey].Version.Should().Be(initial.Version + 1);
        var saved = await values.GetAsync(definition.DefinitionKey, TestContext.Current.CancellationToken);
        saved.Should().NotBeNull();
        saved!.Version.Should().Be(initial.Version + 1);
        saved.Json.Should().Be("{\"Enabled\":false}");
        var rows = await histories.QueryHistoryAsync(null, null, null, null, request.MutationGroup.GroupId,
            TestContext.Current.CancellationToken);
        rows.Should().HaveCount(2).And.OnlyContain(row => row.Version == initial.Version + 1);
        var first = rows.Single(row => row.HistoryId == request.Items[0].Histories[0].HistoryId);
        var second = rows.Single(row => row.HistoryId == request.Items[0].Histories[1].HistoryId);
        first.OldValue!.Json.Should().Be("false");
        first.NewValue.Json.Should().Be("true");
        second.OldValue!.Json.Should().Be("true");
        second.NewValue.Json.Should().Be("false");
        (await histories.GetGroupAsync(request.MutationGroup.GroupId, TestContext.Current.CancellationToken))!
            .MutationCount.Should().Be(2);
    }

    [Fact]
    public async Task CommitAsync_WhenLaterDefinitionHasStaleVersion_ShouldRollBackEveryValueHistoryAndGroup()
    {
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var histories = provider.GetRequiredService<IConfigurationHistoryStore>();
        var batchStore = provider.GetRequiredService<IConfigurationMutationBatchStore>();
        var firstDefinition = CreateDefinition("Test.Batch.Atomic.First");
        var secondDefinition = CreateDefinition("Test.Batch.Atomic.Second");
        var first = await values.EnsureCreatedAsync(firstDefinition, "{\"Enabled\":false}", TestContext.Current.CancellationToken);
        var second = await values.EnsureCreatedAsync(secondDefinition, "{\"Enabled\":false}", TestContext.Current.CancellationToken);
        await values.SaveAsync(new ConfigurationEffectiveValueSaveRequest
        {
            Definition = secondDefinition, Json = "{\"Enabled\":true}", ExpectedVersion = second.Version
        }, TestContext.Current.CancellationToken);
        var request = CreatePairedBatch(firstDefinition, first.Version);
        var secondItem = CreatePairedBatch(secondDefinition, second.Version).Items[0];
        request = request with { Items = [request.Items[0], secondItem] };

        var act = () => batchStore.CommitAsync(request, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ConfigurationConcurrencyConflictException>();
        var unchanged = await values.GetAsync(firstDefinition.DefinitionKey, TestContext.Current.CancellationToken);
        unchanged!.Version.Should().Be(first.Version);
        unchanged.Json.Should().Be(first.Json);
        (await histories.QueryHistoryAsync(null, null, firstDefinition.DefinitionKey, null, null,
            TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await histories.GetGroupAsync(request.MutationGroup.GroupId, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task CommitAsync_WhenSameGroupIsRetried_ShouldNotIncrementVersionOrDuplicateAuditRows()
    {
        var databasePath = await CreateMigratedDatabaseAsync();
        await using var provider = CreateProvider(databasePath);
        var values = provider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var histories = provider.GetRequiredService<IConfigurationHistoryStore>();
        var batchStore = provider.GetRequiredService<IConfigurationMutationBatchStore>();
        var definition = CreateDefinition("Test.Batch.Idempotent");
        var initial = await values.EnsureCreatedAsync(definition, "{\"Enabled\":false}", TestContext.Current.CancellationToken);
        var request = CreatePairedBatch(definition, initial.Version);

        await batchStore.CommitAsync(request, TestContext.Current.CancellationToken);
        var retried = await batchStore.CommitAsync(request, TestContext.Current.CancellationToken);

        retried.AppliedRequestIds.Should().Equal("enable", "disable");
        retried.Documents[definition.DefinitionKey].Version.Should().Be(initial.Version + 1);
        (await histories.QueryHistoryAsync(null, null, null, null, request.MutationGroup.GroupId,
            TestContext.Current.CancellationToken)).Should().HaveCount(2);
        (await histories.ListGroupsAsync(null, null, null, TestContext.Current.CancellationToken))
            .Should().ContainSingle(group => group.GroupId == request.MutationGroup.GroupId);
    }

    private static ConfigurationMutationBatchCommitRequest CreatePairedBatch(ConfigurationDefinition definition, long version)
    {
        var groupId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        return new ConfigurationMutationBatchCommitRequest
        {
            MutationGroup = new ConfigurationMutationGroup
            {
                GroupId = groupId, Label = "Paired changes", DefinitionKeys = [definition.DefinitionKey],
                MutationCount = 2, CreatedTime = timestamp
            },
            Items =
            [
                new ConfigurationMutationBatchCommitItem
                {
                    RequestIds = ["enable", "disable"],
                    SaveRequest = new ConfigurationEffectiveValueSaveRequest
                    {
                        Definition = definition, Json = "{\"Enabled\":false}", ExpectedVersion = version,
                        Context = new ConfigurationMutationContext { MutationGroupId = groupId }
                    },
                    Histories = new[] { (Old: "false", New: "true"), (Old: "true", New: "false") }
                        .Select((stage, index) => new ConfigurationValueHistory
                        {
                            HistoryId = Guid.NewGuid().ToString("N"), DefinitionKey = definition.DefinitionKey,
                            LogicalPath = LogicalPath.FromProperties("Enabled"),
                            OldValue = ConfigurationStoredValue.FromJson(stage.Old),
                            NewValue = ConfigurationStoredValue.FromJson(stage.New), Version = version + 1,
                            SchemaVersion = definition.SchemaVersion, SchemaHash = definition.SchemaHash,
                            MutationGroupId = groupId, ModifiedTime = timestamp.AddTicks(index)
                        }).ToArray()
                }
            ]
        };
    }
}
