using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Models;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationMutationObjectValidationTests
{
    [Fact]
    public async Task ApplyAsync_WhenPairedEditsRepairTransientlyInvalidAggregate_ShouldSaveOnceAndShareHistoryVersion()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        var before = await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken);
        var request = Group(fixture.Set("minimum", "Minimum", 30), fixture.Set("maximum", "Maximum", 40));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request with { ExpectedValidationFingerprint = preview.ValidationFingerprint },
            TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeTrue();
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Applied);
        fixture.Values.SaveCount.Should().Be(1);
        result.Outcomes.Should().HaveCount(2).And.OnlyContain(outcome => outcome.Result!.NewVersion == before!.Version + 1);
        result.MutationGroup.Should().NotBeNull();
        var histories = await fixture.History.QueryHistoryAsync(null, null, null, null, result.MutationGroup!.GroupId,
            TestContext.Current.CancellationToken);
        histories.Should().HaveCount(2).And.OnlyContain(history => history.Version == before!.Version + 1);
        histories.Single(history => history.LogicalPath == LogicalPath.FromProperties("Minimum")).OldValue!.Json.Should().Be("10");
        histories.Single(history => history.LogicalPath == LogicalPath.FromProperties("Maximum")).OldValue!.Json.Should().Be("20");
        fixture.Configuration["ObjectMutation:Minimum"].Should().Be("30");
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("40");
        await fixture.Notifier.Received(1).NotifyAsync(Arg.Is<ConfigurationReloadSignal>(signal =>
            signal.Definitions.Count == 1 && signal.Definitions[0].Version == before!.Version + 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_WhenFinalAggregateIsInvalid_ShouldRejectBeforeEveryPersistenceAndReloadSideEffect()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        var before = await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken);
        var request = Group(fixture.Set("minimum", "Minimum", 30));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request, TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        preview.Definitions.Where(report => report.Target == ConfigurationCandidateValidationTarget.EffectiveAggregate)
            .Should().ContainSingle().Which.Coverage.Should().Be(ConfigurationValidationCoverage.Complete);
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        result.MutationGroup.Should().BeNull();
        result.Outcomes.Should().OnlyContain(outcome => outcome.Status == ConfigurationMutationOutcomeStatus.Skipped);
        await AssertNoMutationSideEffectsAsync(fixture);
        (await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task PreviewAsync_WhenEffectiveStoreCandidateIsMaskedByValidHigherSource_ShouldStillRejectInvalidStoredAggregate()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync("{\"ObjectMutation\":{\"Minimum\":5}}");
        var request = Group(fixture.Set("minimum", "Minimum", 25));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request, TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        await AssertNoMutationSideEffectsAsync(fixture);
        fixture.Configuration["ObjectMutation:Minimum"].Should().Be("5");
    }

    [Fact]
    public async Task PreviewAsync_WhenHigherSourceMakesOtherwiseValidStoreCandidateInvalid_ShouldPreserveProviderPriority()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync("{\"ObjectMutation\":{\"Maximum\":12}}");
        var request = Group(fixture.Set("minimum", "Minimum", 15), fixture.Set("maximum", "Maximum", 30));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        preview.Definitions.Where(report => report.Target == ConfigurationCandidateValidationTarget.EffectiveAggregate)
            .Should().ContainSingle().Which.Issues.Should().NotBeEmpty();
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("12");
        await AssertNoMutationSideEffectsAsync(fixture);
    }

    [Theory]
    [InlineData("Minimum", "Maximum")]
    [InlineData("minimum", "maximum")]
    public async Task ApplyAsync_WhenRemovingExternalValueRevealsInvalidFallback_ShouldRejectAndKeepPhysicalSourceUnchanged(
        string minimumKey, string maximumKey)
    {
        var sourceJson = $"{{\"ObjectMutation\":{{\"{minimumKey}\":5,\"{maximumKey}\":20}}}}";
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(sourceJson);
        await fixture.SetStoredBaselineAsync("{\"Minimum\":25,\"Maximum\":20}");
        var sourceBefore = await fixture.ReadSourceAsync(0);
        var remove = fixture.Set("remove-minimum", "Minimum", 0, fixture.Source(0)) with
        {
            MutationKind = ConfigurationMutationKind.Remove, Value = ConfigurationStoredValue.Null
        };

        var preview = await fixture.Mutations.PreviewAsync(Group(remove), TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(Group(remove), TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        (await fixture.ReadSourceAsync(0)).Should().Be(sourceBefore);
        fixture.Configuration["ObjectMutation:Minimum"].Should().Be("5");
        await AssertNoMutationSideEffectsAsync(fixture);
    }

    [Fact]
    public async Task ApplyAsync_WhenTwoSourcesAreValidOnlyAfterBothAdopt_ShouldRejectUnsafeIndependentAdoption()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"Minimum\":10}}", "{\"ObjectMutation\":{\"Maximum\":20}}");
        var firstBefore = await fixture.ReadSourceAsync(0);
        var secondBefore = await fixture.ReadSourceAsync(1);
        var request = Group(fixture.Set("minimum", "Minimum", 30, fixture.Source(0)),
            fixture.Set("maximum", "Maximum", 40, fixture.Source(1)));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request, TestContext.Current.CancellationToken);

        preview.Definitions.Should().ContainSingle().Which.IsValid.Should().BeTrue();
        preview.Problems.Should().Contain(problem => problem.Code == "UnsafeIndependentAdoption");
        preview.CanApply.Should().BeFalse();
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        (await fixture.ReadSourceAsync(0)).Should().Be(firstBefore);
        (await fixture.ReadSourceAsync(1)).Should().Be(secondBefore);
        await AssertNoMutationSideEffectsAsync(fixture);
    }

    [Fact]
    public async Task ApplyAsync_WhenUntargetedPhysicalSourceChangedButProviderIsStale_ShouldRejectBeforeFullReloadCanAdoptUnplannedValue()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"Minimum\":10}}", "{\"ObjectMutation\":{\"Maximum\":20}}");
        var firstBefore = await fixture.ReadSourceAsync(0);
        await fixture.ChangePhysicalSourceWithoutReloadAsync(1, "{\"ObjectMutation\":{\"Maximum\":12}}");
        var secondBefore = await fixture.ReadSourceAsync(1);
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("20");
        var request = Group(fixture.Set("minimum", "Minimum", 15, fixture.Source(0)));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request, TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        preview.Problems.Should().Contain(problem => problem.Code == "RuntimeOutOfSync");
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        (await fixture.ReadSourceAsync(0)).Should().Be(firstBefore);
        (await fixture.ReadSourceAsync(1)).Should().Be(secondBefore);
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("20");
        await AssertNoMutationSideEffectsAsync(fixture);
    }

    [Fact]
    public async Task ApplyAsync_WhenStoreChangedButProjectionIsStale_ShouldRejectWithoutEffectsAndAcceptSafeEditAfterExplicitReload()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"Minimum\":10}}");
        var sourceBefore = await fixture.ReadSourceAsync(0);
        var loadedVersion = fixture.Reloads.GetLoadedMonicaProjectionVersion(fixture.Definition.DefinitionKey)
            ?? throw new InvalidOperationException("The started host must have a loaded store projection.");
        await fixture.ChangeStoredDocumentWithoutReloadAsync("{\"Maximum\":12}");
        var storedBefore = await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The started host must have a stored document.");
        storedBefore.Version.Should().Be(loadedVersion + 1);
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("20");
        var unsafeRequest = Group(fixture.Set("minimum", "Minimum", 15, fixture.Source(0)));

        var stalePreview = await fixture.Mutations.PreviewAsync(unsafeRequest, TestContext.Current.CancellationToken);
        var rejected = await fixture.Mutations.ApplyAsync(unsafeRequest, TestContext.Current.CancellationToken);

        stalePreview.CanApply.Should().BeFalse();
        stalePreview.Problems.Should().Contain(problem => problem.Code == "RuntimeOutOfSync");
        rejected.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        (await fixture.ReadSourceAsync(0)).Should().Be(sourceBefore);
        (await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(storedBefore);
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("20");
        await AssertNoMutationSideEffectsAsync(fixture);

        await fixture.Reloads.ReloadMonicaProjectionAsync(fixture.Definition.DefinitionKey, storedBefore.Version,
            TestContext.Current.CancellationToken);
        fixture.ResetObservations();
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("12");
        var safeRequest = Group(fixture.Set("minimum", "Minimum", 11, fixture.Source(0)));
        var freshPreview = await fixture.Mutations.PreviewAsync(safeRequest, TestContext.Current.CancellationToken);
        var applied = await fixture.Mutations.ApplyAsync(safeRequest with
        {
            ExpectedValidationFingerprint = freshPreview.ValidationFingerprint
        }, TestContext.Current.CancellationToken);

        freshPreview.CanApply.Should().BeTrue();
        applied.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Applied);
        applied.PostCommitIssues.Should().BeEmpty();
        fixture.Writer.WriteCount.Should().Be(1);
        fixture.Values.SaveCount.Should().Be(0);
        fixture.Configuration["ObjectMutation:Minimum"].Should().Be("11");
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("12");
        fixture.Reloads.GetLoadedMonicaProjectionVersion(fixture.Definition.DefinitionKey).Should().Be(storedBefore.Version);
        applied.MutationGroup.Should().NotBeNull();
        (await fixture.History.QueryHistoryAsync(null, null, null, null, applied.MutationGroup!.GroupId,
            TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyAsync_WhenLoadedStoreContributionChangesWithoutVersionChange_ShouldRejectBeforeReloadCanReplaceIt()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"Minimum\":10}}");
        await fixture.SetStoredBaselineAsync("{\"Maximum\":12}");
        var storedBefore = await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The started host must have a stored document.");
        var sourceBefore = await fixture.ReadSourceAsync(0);
        fixture.ChangeLoadedStoreContribution("Maximum", "20");
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("20");
        fixture.Reloads.GetLoadedMonicaProjectionVersion(fixture.Definition.DefinitionKey).Should().Be(storedBefore.Version);
        var request = Group(fixture.Set("minimum", "Minimum", 15, fixture.Source(0)));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request, TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        preview.Problems.Should().Contain(problem => problem.Code == "RuntimeOutOfSync");
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        (await fixture.ReadSourceAsync(0)).Should().Be(sourceBefore);
        (await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken))
            .Should().BeEquivalentTo(storedBefore);
        await AssertNoMutationSideEffectsAsync(fixture);
    }

    [Fact]
    public async Task ApplyAsync_WhenEveryIndependentSourceAdoptionIsSafe_ShouldAcceptActualFirstWriteReloadAndCommitSecondSource()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"Minimum\":10}}", "{\"ObjectMutation\":{\"Maximum\":20}}");
        var adoptedValues = new List<(string? Minimum, string? Maximum)>();
        fixture.Writer.AfterWrite = source =>
        {
            fixture.AdoptWrittenSource(source);
            adoptedValues.Add((fixture.Configuration["ObjectMutation:Minimum"], fixture.Configuration["ObjectMutation:Maximum"]));
        };
        var request = Group(fixture.Set("minimum", "Minimum", 15, fixture.Source(0)),
            fixture.Set("maximum", "Maximum", 25, fixture.Source(1)));

        var preview = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        var result = await fixture.Mutations.ApplyAsync(request with { ExpectedValidationFingerprint = preview.ValidationFingerprint },
            TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeTrue();
        preview.Problems.Should().BeEmpty();
        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Applied);
        result.PostCommitIssues.Should().BeEmpty();
        result.Outcomes.Should().HaveCount(2).And.OnlyContain(outcome => outcome.Status == ConfigurationMutationOutcomeStatus.Applied);
        fixture.Writer.WriteCount.Should().Be(2);
        fixture.Values.SaveCount.Should().Be(0);
        adoptedValues.Should().Equal(("15", "20"), ("15", "25"));
        JsonNode.Parse(await fixture.ReadSourceAsync(0))!["ObjectMutation"]!["Minimum"]!.GetValue<int>().Should().Be(15);
        JsonNode.Parse(await fixture.ReadSourceAsync(1))!["ObjectMutation"]!["Maximum"]!.GetValue<int>().Should().Be(25);
        result.MutationGroup.Should().NotBeNull();
        (await fixture.History.QueryHistoryAsync(null, null, null, null, result.MutationGroup!.GroupId,
            TestContext.Current.CancellationToken)).Should().HaveCount(2);
    }

    [Fact]
    public async Task PreviewAsync_WhenFullAndEverySingleAdoptionAreValidButPairIsInvalid_ShouldInspectAllNonemptySubsets()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"StageA\":false}}", "{\"ObjectMutation\":{\"StageB\":false}}",
            "{\"ObjectMutation\":{\"StageC\":false}}");
        var commands = new[] { "StageA", "StageB", "StageC" }.Select((property, index) =>
            fixture.Set(property, property, 0, fixture.Source(index)) with { Value = ConfigurationStoredValue.FromJson("true") }).ToArray();
        foreach (var command in commands)
            (await fixture.Mutations.PreviewAsync(Group(command), TestContext.Current.CancellationToken)).CanApply.Should().BeTrue();

        var full = await fixture.Mutations.PreviewAsync(Group(commands), TestContext.Current.CancellationToken);

        full.Definitions.Should().ContainSingle().Which.IsValid.Should().BeTrue();
        full.Problems.Should().Contain(problem => problem.Code == "UnsafeIndependentAdoption");
        full.CanApply.Should().BeFalse();
        await AssertNoMutationSideEffectsAsync(fixture);
    }

    [Fact]
    public async Task ApplyAsync_WhenExternalAliasIsExplicitlyNull_ShouldKeepNullShapeAndUseAliasForObjectValidation()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync(
            "{\"ObjectMutation\":{\"note-text\":\"initial\"}}");
        var command = fixture.Set("note", "note-text", 0, fixture.Source(0));
        var invalid = command with { Value = ConfigurationStoredValue.FromJson("\"blocked\"") };
        var invalidPreview = await fixture.Mutations.PreviewAsync(Group(invalid), TestContext.Current.CancellationToken);
        invalidPreview.CanApply.Should().BeFalse();
        invalidPreview.Definitions.Should().ContainSingle().Which.Issues.Should().Contain(issue => issue.LogicalPath.ToCanonicalString() == "note-text");
        await AssertNoMutationSideEffectsAsync(fixture);

        var result = await fixture.Mutations.ApplyAsync(Group(command with { Value = ConfigurationStoredValue.Null }),
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Applied);
        fixture.Values.SaveCount.Should().Be(0);
        var written = JsonNode.Parse(await fixture.ReadSourceAsync(0))!["ObjectMutation"]!.AsObject();
        written.ContainsKey("note-text").Should().BeTrue();
        written["note-text"].Should().BeNull();
        fixture.Configuration["ObjectMutation:note-text"].Should().BeNull();
    }

    [Fact]
    public async Task ApplyAsync_WhenUneditedSiblingChangesAfterPreview_ShouldRejectStaleWholeAggregateFingerprint()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        var request = Group(fixture.Set("minimum", "Minimum", 15));
        var reviewed = await fixture.Mutations.PreviewAsync(request, TestContext.Current.CancellationToken);
        await fixture.SetStoredBaselineAsync("{\"Minimum\":10,\"Maximum\":25}");

        var result = await fixture.Mutations.ApplyAsync(request with { ExpectedValidationFingerprint = reviewed.ValidationFingerprint },
            TestContext.Current.CancellationToken);

        result.Status.Should().Be(ConfigurationMutationGroupApplyStatus.Rejected);
        result.ValidationPreview!.Problems.Should().Contain(problem => problem.Code == "StaleAggregate");
        await AssertNoMutationSideEffectsAsync(fixture);
        fixture.Configuration["ObjectMutation:Maximum"].Should().Be("25");
    }

    [Fact]
    public async Task RollbackHistoryAsync_WhenHistoricalValueViolatesCurrentObjectCode_ShouldBlockWithoutAuditOrReload()
    {
        await using var fixture = await ConfigurationMutationObjectValidationFixture.CreateAsync();
        var current = await fixture.Values.GetAsync(fixture.Definition.DefinitionKey, TestContext.Current.CancellationToken);
        var history = new ConfigurationValueHistory
        {
            HistoryId = Guid.NewGuid().ToString("N"), DefinitionKey = fixture.Definition.DefinitionKey,
            LogicalPath = LogicalPath.FromProperties("Minimum"), OldValue = ConfigurationStoredValue.FromJson("30"),
            NewValue = ConfigurationStoredValue.FromJson("10"), Version = current!.Version,
            SchemaVersion = fixture.Definition.SchemaVersion, SchemaHash = fixture.Definition.SchemaHash,
            ModifiedTime = DateTimeOffset.UtcNow, TargetKind = ConfigurationMutationTargetKind.MonicaEffectiveStore
        };
        await fixture.History.AppendHistoryAsync(history, TestContext.Current.CancellationToken);
        var rollback = fixture.Services.GetRequiredService<IConfigurationRollbackService>();

        var preview = await rollback.PreviewHistoriesAsync([history.HistoryId], TestContext.Current.CancellationToken);
        var act = () => rollback.RollbackHistoryAsync(history.HistoryId, preview.PlanToken, new ConfigurationMutationContext(),
            TestContext.Current.CancellationToken);

        preview.CanApply.Should().BeFalse();
        preview.ValidationReports.Where(report => report.Target == ConfigurationCandidateValidationTarget.EffectiveAggregate)
            .Should().ContainSingle().Which.Issues.Should().NotBeEmpty();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Rejected*");
        fixture.Values.SaveCount.Should().Be(0);
        fixture.Reloads.ReloadCount.Should().Be(0);
        fixture.Writer.WriteCount.Should().Be(0);
        (await fixture.History.QueryHistoryAsync(null, null, null, null, null, TestContext.Current.CancellationToken)).Should().ContainSingle();
        (await fixture.History.ListGroupsAsync(null, null, null, TestContext.Current.CancellationToken)).Should().BeEmpty();
        await fixture.Notifier.DidNotReceive().NotifyAsync(Arg.Any<ConfigurationReloadSignal>(), Arg.Any<CancellationToken>());
    }

    private static ConfigurationMutationGroupApplyRequest Group(params ConfigurationMutationCommand[] commands) => new()
    {
        Label = "Object validation behavior", Commands = commands
    };

    private static async Task AssertNoMutationSideEffectsAsync(ConfigurationMutationObjectValidationFixture fixture)
    {
        fixture.Values.SaveCount.Should().Be(0);
        fixture.Reloads.ReloadCount.Should().Be(0);
        fixture.Writer.WriteCount.Should().Be(0);
        (await fixture.History.QueryHistoryAsync(null, null, null, null, null, TestContext.Current.CancellationToken)).Should().BeEmpty();
        (await fixture.History.ListGroupsAsync(null, null, null, TestContext.Current.CancellationToken)).Should().BeEmpty();
        await fixture.Notifier.DidNotReceive().NotifyAsync(Arg.Any<ConfigurationReloadSignal>(), Arg.Any<CancellationToken>());
    }
}
