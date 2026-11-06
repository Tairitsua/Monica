using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.AI.Abstractions;
using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.Chat.Abstractions;
using Monica.AI.Chat.Facades;
using Monica.AI.Chat.Models;
using Monica.AI.Models;
using Monica.AI.Services;
using Monica.AI.Services.Support;
using Monica.Core.Results;
using Monica.Modules;
using NSubstitute;
using Test.Monica.AI.Support;

namespace Test.Monica.AI.Chat.Facades;

public sealed class ChatHistoryFacadeTests
{
    [Fact]
    public async Task LoadThenSave_WhenIdentityChanges_ShouldRejectCrossPartitionWrite()
    {
        var provider = Substitute.For<IChatHistoryProvider>();
        var resolver = Substitute.For<IChatHistoryPartitionResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new ChatHistoryPartition("owner"));
        provider.LoadSessionAsync(Arg.Any<ChatHistoryPartition>(), "session", Arg.Any<CancellationToken>()).Returns(CreateSnapshot());
        var facade = new ChatHistoryFacade(provider, resolver, CreateService());
        var loaded = await facade.LoadSessionAsync("session", TestContext.Current.CancellationToken);
        loaded.Status.Should().Be(ResultStatus.Ok);
        loaded.Message.Should().BeNull();
        loaded.Data.Should().NotBeNull();
        await using var session = loaded.Data!;
        session.Partition!.Key.Should().Be("owner");
        session.IsRuntimeActive.Should().BeFalse();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(new ChatHistoryPartition("other-user"));

        var result = await facade.SaveSessionAsync(session, 0, TestContext.Current.CancellationToken);

        result.IsFailure().Should().BeTrue();
        result.Message.Should().Contain("different user or workspace");
        await provider.DidNotReceive().SaveSessionAsync(Arg.Any<ChatHistoryPartition>(), Arg.Any<ChatSessionSnapshot>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCatalog_WhenCancelled_ShouldPropagateCancellationWithoutFailureEnvelope()
    {
        var resolver = Substitute.For<IChatHistoryPartitionResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException<ChatHistoryPartition>(new OperationCanceledException()));
        var facade = new ChatHistoryFacade(Substitute.For<IChatHistoryProvider>(), resolver, CreateService());
        var action = async () => await facade.GetCatalogAsync(TestContext.Current.CancellationToken);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("archive")]
    [InlineData("delete")]
    public async Task Organization_WhenRequested_ShouldResolveTrustedPartitionAndExposeProviderOutcome(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        var partition = new ChatHistoryPartition("trusted/workspace/user");
        var provider = Substitute.For<IChatHistoryProvider>();
        var resolver = Substitute.For<IChatHistoryPartitionResolver>();
        resolver.ResolveAsync(ct).Returns(partition);
        var outcome = new ChatHistoryWriteResult
        {
            Status = ChatHistoryWriteStatus.Succeeded, Revision = 8, Warning = "Physical cleanup is pending."
        };
        provider.SetPinnedAsync(partition, "session", true, 7, ct).Returns(outcome);
        provider.SetArchivedAsync(partition, Arg.Any<IReadOnlyList<string>>(), true, 7, ct).Returns(outcome);
        provider.DeleteArchivedSessionsAsync(partition, Arg.Any<IReadOnlyList<string>>(), 7, ct).Returns(outcome);
        var facade = new ChatHistoryFacade(provider, resolver, CreateService());

        var result = operation switch
        {
            "pin" => await facade.SetPinnedAsync("session", true, 7, ct),
            "archive" => await facade.SetArchivedAsync(["session"], true, 7, ct),
            _ => await facade.DeleteArchivedSessionsAsync(["session"], 7, ct)
        };

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().BeSameAs(outcome);
        await resolver.Received(1).ResolveAsync(ct);
    }

    private static ChatSessionSnapshot CreateSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return new ChatSessionSnapshot { SessionId = "session", Title = "Saved", CreatedAt = now, UpdatedAt = now,
            Settings = new ChatSessionSettings("provider"), Turns = [] };
    }

    private static AIChatService CreateService() => new(Substitute.For<IAIProviderFactory>(), Options.Create(new ModuleAIOption()),
        new TestAIChatAgentFactory(static (_, _, _) => throw new InvalidOperationException("History must not activate an agent.")),
        Substitute.For<IAgentCapabilityStateStore>(), new AgentStreamingCoordinator(new AIChatRuntimeContextAccessor(), new AgentResponseUpdateChannelContext()),
        Substitute.For<IChatAttachmentStore>());
}
