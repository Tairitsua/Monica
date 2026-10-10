using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationPostCommitDiagnosticsTests
{
    private const string OUTER_SENTINEL = "private-endpoint-and-credential";
    private const string INNER_SENTINEL = "private-inner-transport-detail";

    [Fact]
    public async Task ApplyAsync_WhenNotificationFails_ShouldKeepCommittedValueAndReturnOnlySafeDiagnostics()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        fixture.Notifier.NotifyAsync(Arg.Any<ConfigurationReloadSignal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(CreatePrivateFailure()));

        var result = await fixture.Mutations.ApplyAsync(new ConfigurationMutationGroupApplyRequest
        {
            Label = "Notification diagnostic", Commands = [fixture.Set("minimum", "Minimum", 11)]
        }, TestContext.Current.CancellationToken);

        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Applied);
        fixture.Values.SaveCount.Should().Be(1);
        fixture.Configuration["ObjectMutation:Minimum"].Should().Be("11");
        var issue = result.PostCommitIssues.Should().ContainSingle().Which;
        issue.Kind.Should().Be(ConfigurationPostCommitIssueKind.DistributedNotification);
        AssertSafe(issue);
    }

    [Fact]
    public async Task BroadcastAllAsync_WhenLocalReloadFails_ShouldReturnOnlySafeDiagnosticsAndStillNotify()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        fixture.Reloads.FullProjectionReloadFailure = CreatePrivateFailure();
        var before = await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken);

        var result = await fixture.Services.GetRequiredService<IConfigurationReloadBroadcastService>()
            .BroadcastAllAsync(TestContext.Current.CancellationToken);

        result.LocalReloadSucceeded.Should().BeFalse();
        var issue = result.PostCommitIssues.Should().ContainSingle().Which;
        issue.Kind.Should().Be(ConfigurationPostCommitIssueKind.LocalReload);
        AssertSafe(issue);
        fixture.Values.SaveCount.Should().Be(0);
        (await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(before);
        await fixture.Notifier.Received(1).NotifyAsync(Arg.Is<ConfigurationReloadSignal>(signal =>
            signal.Kind == ConfigurationReloadSignalKind.ReloadAll), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RollbackGroupAsync_WhenOriginalAuditMarkerFails_ShouldKeepRealRollbackAndReturnOnlySafeDiagnostics()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        var original = await fixture.Mutations.ApplyAsync(new ConfigurationMutationGroupApplyRequest
        {
            Label = "Original mutation", Commands = [fixture.Set("minimum", "Minimum", 11)]
        }, TestContext.Current.CancellationToken);
        var group = original.MutationGroup ?? throw new InvalidOperationException("The mutation must persist its group.");
        var histories = await fixture.History.QueryHistoryAsync(null, null, null, null, group.GroupId,
            TestContext.Current.CancellationToken);
        var groups = Substitute.For<IConfigurationMutationGroupService>();
        groups.GetAsync(group.GroupId, Arg.Any<CancellationToken>()).Returns(group);
        groups.GetGroupHistoryAsync(group.GroupId, Arg.Any<CancellationToken>()).Returns(histories);
        groups.MarkRolledBackAsync(group.GroupId, Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(CreatePrivateFailure()));
        var rollback = new ConfigurationRollbackService(
            fixture.Services.GetRequiredService<IConfigurationHistoryService>(), fixture.Mutations, fixture.Values,
            fixture.Services.GetRequiredService<IConfigurationSourceInspector>(), fixture.Writer, groups,
            fixture.Services.GetRequiredService<ConfigurationDefinitionResolver>(),
            fixture.Services.GetRequiredService<ConfigurationEffectiveValueDocumentEditor>(),
            fixture.Services.GetRequiredService<ConfigurationPathProjector>(), NullLogger<ConfigurationRollbackService>.Instance);
        var preview = await rollback.PreviewHistoriesAsync(histories.Select(history => history.HistoryId).ToArray(),
            TestContext.Current.CancellationToken);
        fixture.ResetObservations();

        var results = await rollback.RollbackGroupAsync(group.GroupId, preview.PlanToken, new ConfigurationMutationContext(),
            TestContext.Current.CancellationToken);

        var result = results.Should().ContainSingle().Which;
        fixture.Values.SaveCount.Should().Be(1);
        fixture.Configuration["ObjectMutation:Minimum"].Should().Be("10");
        var issue = result.PostCommitIssues.Should().ContainSingle().Which;
        issue.Kind.Should().Be(ConfigurationPostCommitIssueKind.AuditFinalization);
        AssertSafe(issue);
        (await fixture.History.QueryHistoryAsync(null, null, null, null, null, TestContext.Current.CancellationToken))
            .Should().HaveCount(2);
    }

    private static Exception CreatePrivateFailure() => new InvalidOperationException(
        OUTER_SENTINEL, new IOException(INNER_SENTINEL));

    private static void AssertSafe(ConfigurationPostCommitIssue issue)
    {
        var diagnostics = $"{issue.Message}|{issue.Detail}";
        diagnostics.Should().NotContain(OUTER_SENTINEL).And.NotContain(INNER_SENTINEL)
            .And.NotContain(nameof(InvalidOperationException)).And.NotContain(nameof(IOException));
        issue.Detail.Should().NotBeNullOrWhiteSpace();
    }
}
