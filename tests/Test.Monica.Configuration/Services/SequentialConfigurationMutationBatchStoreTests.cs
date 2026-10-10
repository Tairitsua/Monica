using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class SequentialConfigurationMutationBatchStoreTests
{
    [Fact]
    public async Task CommitAsync_WhenDefinitionContainsTwoCommands_ShouldSaveFinalDocumentOnceAndAppendBothHistories()
    {
        var token = TestContext.Current.CancellationToken;
        var values = Substitute.For<IConfigurationEffectiveValueStore>();
        var histories = Substitute.For<IConfigurationHistoryStore>();
        var item = CreateItem("test.range", 7, "minimum", "maximum");
        ConfigureSavedDocuments(values);
        var store = CreateStore(values, histories);

        var result = await store.CommitAsync(CreateRequest(item), token);

        await values.Received(1).SaveAsync(item.SaveRequest, token);
        result.AppliedRequestIds.Should().Equal("minimum", "maximum");
        result.Documents["test.range"].Version.Should().Be(8);
        result.Documents["test.range"].Json.Should().Be(item.SaveRequest.Json);
        result.MutationGroup.MutationCount.Should().Be(2);
        result.Failure.Should().BeNull();
        result.PostCommitIssues.Should().BeEmpty();
        histories.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IConfigurationHistoryStore.AppendHistoryAsync))
            .Select(call => (ConfigurationValueHistory)call.GetArguments()[0]!).Should().Equal(item.Histories);
        await histories.Received(1).UpsertGroupAsync(result.MutationGroup, token);
    }

    [Fact]
    public async Task CommitAsync_WhenDefinitionSaveFails_ShouldLeaveAllItsCommandsAndFollowingDefinitionsUnapplied()
    {
        var token = TestContext.Current.CancellationToken;
        var values = Substitute.For<IConfigurationEffectiveValueStore>();
        var histories = Substitute.For<IConfigurationHistoryStore>();
        var applied = CreateItem("test.applied", 1, "applied-a", "applied-b");
        var failed = CreateItem("test.failed", 1, "failed-a", "failed-b");
        var skipped = CreateItem("test.skipped", 1, "skipped-a", "skipped-b");
        ConfigureSavedDocuments(values);
        values.SaveAsync(failed.SaveRequest, token)
            .Returns(Task.FromException<ConfigurationEffectiveValueDocument>(
                new IOException("Private candidate sentinel", new InvalidOperationException("Private inner sentinel"))));

        var result = await CreateStore(values, histories).CommitAsync(CreateRequest(applied, failed, skipped), token);

        result.AppliedRequestIds.Should().Equal("applied-a", "applied-b");
        result.Failure.Should().NotBeNull();
        result.Failure!.RequestId.Should().Be("failed-a");
        result.Failure.Message.Should().NotContain("Private");
        result.Failure.Detail.Should().NotContain("Private");
        result.Documents.Keys.Should().Equal("test.applied");
        result.MutationGroup.MutationCount.Should().Be(2);
        result.MutationGroup.Status.Should().Be(ConfigurationMutationGroupStatus.PartiallyApplied);
        await values.DidNotReceive().SaveAsync(skipped.SaveRequest, token);
        foreach (var history in failed.Histories.Concat(skipped.Histories))
            await histories.DidNotReceive().AppendHistoryAsync(history, token);
    }

    [Fact]
    public async Task CommitAsync_WhenOneHistoryAppendFails_ShouldKeepAppliedCommandsAndAttemptRemainingAuditRows()
    {
        var token = TestContext.Current.CancellationToken;
        var values = Substitute.For<IConfigurationEffectiveValueStore>();
        var histories = Substitute.For<IConfigurationHistoryStore>();
        var item = CreateItem("test.audit", 1, "first", "second");
        ConfigureSavedDocuments(values);
        histories.AppendHistoryAsync(item.Histories[0], token)
            .Returns(Task.FromException(new IOException("Private audit sentinel")));

        var result = await CreateStore(values, histories).CommitAsync(CreateRequest(item), token);

        result.AppliedRequestIds.Should().Equal("first", "second");
        result.Failure.Should().BeNull();
        result.PostCommitIssues.Should().ContainSingle().Which.Kind.Should().Be(ConfigurationPostCommitIssueKind.AuditFinalization);
        result.PostCommitIssues[0].Message.Should().NotContain("Private");
        result.PostCommitIssues[0].Detail.Should().NotContain("Private");
        await histories.Received(1).AppendHistoryAsync(item.Histories[1], token);
        await histories.Received(1).UpsertGroupAsync(result.MutationGroup, token);
    }

    private static SequentialConfigurationMutationBatchStore CreateStore(
        IConfigurationEffectiveValueStore values, IConfigurationHistoryStore histories)
        => new(values, histories, NullLogger<SequentialConfigurationMutationBatchStore>.Instance);

    private static void ConfigureSavedDocuments(IConfigurationEffectiveValueStore store)
        => store.SaveAsync(Arg.Any<ConfigurationEffectiveValueSaveRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var save = call.Arg<ConfigurationEffectiveValueSaveRequest>();
                return Task.FromResult(new ConfigurationEffectiveValueDocument
                {
                    DefinitionKey = save.Definition.DefinitionKey,
                    Json = save.Json,
                    Version = save.ExpectedVersion!.Value + 1,
                    SchemaVersion = save.Definition.SchemaVersion,
                    LastModifiedTime = DateTimeOffset.UnixEpoch
                });
            });

    private static ConfigurationMutationBatchCommitRequest CreateRequest(params ConfigurationMutationBatchCommitItem[] items)
        => new()
        {
            Items = items,
            MutationGroup = new ConfigurationMutationGroup
            {
                GroupId = "test-group",
                Label = "Final document batch",
                DefinitionKeys = items.Select(item => item.SaveRequest.Definition.DefinitionKey).ToArray(),
                MutationCount = items.Sum(item => item.RequestIds.Count),
                CreatedTime = DateTimeOffset.UnixEpoch,
                Status = ConfigurationMutationGroupStatus.Applied
            }
        };

    private static ConfigurationMutationBatchCommitItem CreateItem(string definitionKey, long version, params string[] requestIds)
    {
        var definition = TestConfigurationFactory.Definition() with { DefinitionKey = definitionKey };
        return new ConfigurationMutationBatchCommitItem
        {
            RequestIds = requestIds,
            SaveRequest = new ConfigurationEffectiveValueSaveRequest
            {
                Definition = definition,
                Json = """{"WorkerId":30}""",
                ExpectedVersion = version,
                Context = new ConfigurationMutationContext { MutationGroupId = "test-group" }
            },
            Histories = requestIds.Select((requestId, index) => new ConfigurationValueHistory
            {
                HistoryId = requestId,
                DefinitionKey = definitionKey,
                LogicalPath = LogicalPath.FromProperties("WorkerId"),
                OldValue = ConfigurationStoredValue.FromJson(index == 0 ? "20" : "10"),
                NewValue = ConfigurationStoredValue.FromJson(index == 0 ? "10" : "30"),
                Version = version + 1,
                SchemaVersion = definition.SchemaVersion,
                SchemaHash = definition.SchemaHash,
                ModifiedTime = DateTimeOffset.UnixEpoch.AddSeconds(index),
                MutationGroupId = "test-group"
            }).ToArray()
        };
    }
}
