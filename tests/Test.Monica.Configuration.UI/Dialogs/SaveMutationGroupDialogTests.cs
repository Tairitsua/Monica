using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Dialogs;
using Monica.Configuration.UI.State;
using Monica.Modules;
using MudBlazor;
using NSubstitute;
using Test.Monica.Configuration.UI.Infrastructure;

namespace Test.Monica.Configuration.UI.Dialogs;

public sealed class SaveMutationGroupDialogTests
{
    [Fact]
    public async Task Save_WhenConfirmationIsDisabled_ShouldApplyWithoutImpactLookup()
    {
        var impactService = Substitute.For<IConfigurationDefinitionChangeImpactService>();
        var applyService = CreateSuccessfulApplyService();
        await using var context = new ConfigurationUiTestContext(
            CreateFacade(impactService, applyService),
            new ModuleConfigurationUIOption());
        var (surface, dialog) = await ShowSaveDialogAsync(context);

        FindButton(surface, "Group:SaveCount").Click();

        var result = await dialog.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        impactService.ReceivedCalls().Should().BeEmpty();
        applyService.ReceivedCalls().Should().ContainSingle();
    }

    [Fact]
    public async Task Save_WhenConfirmationIsEnabledAndCanceled_ShouldNotApply()
    {
        var impactService = CreateSuccessfulImpactService();
        var applyService = CreateSuccessfulApplyService();
        await using var context = new ConfigurationUiTestContext(
            CreateFacade(impactService, applyService),
            new ModuleConfigurationUIOption { EnableAffectedServiceConfirmation = true });
        var (surface, dialog) = await ShowSaveDialogAsync(context);

        FindButton(surface, "Group:SaveCount").Click();
        surface.WaitForAssertion(() =>
            FindButton(surface, "Dialogs:SaveGroup:Impact:Actions:ConfirmSave").Should().NotBeNull());
        FindLastDialogButton(surface, "Common:Actions:Cancel").Click();

        surface.WaitForAssertion(() =>
            surface.Markup.Should().NotContain("Dialogs:SaveGroup:Impact:Actions:ConfirmSave"));
        applyService.ReceivedCalls().Should().BeEmpty();
        var impactCall = impactService.ReceivedCalls().Single();
        var targets = (IReadOnlyCollection<ConfigurationParameterChangeTarget>)impactCall.GetArguments()[0]!;
        targets.Should().ContainSingle();
        targets.Single().DefinitionKey.Should().Be("Sample.Options");
        targets.Single().LogicalPath.ToCanonicalString().Should().Be("Name");

        FindButton(surface, "Common:Actions:Cancel").Click();
        var result = await dialog.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeTrue();
    }

    [Fact]
    public async Task Save_WhenConfirmationIsEnabledAndApproved_ShouldApplyExistingMutationPath()
    {
        var impactService = CreateSuccessfulImpactService();
        var applyService = CreateSuccessfulApplyService();
        await using var context = new ConfigurationUiTestContext(
            CreateFacade(impactService, applyService),
            new ModuleConfigurationUIOption { EnableAffectedServiceConfirmation = true });
        var (surface, dialog) = await ShowSaveDialogAsync(context);

        FindButton(surface, "Group:SaveCount").Click();
        surface.WaitForAssertion(() =>
            FindButton(surface, "Dialogs:SaveGroup:Impact:Actions:ConfirmSave").Should().NotBeNull());
        FindButton(surface, "Dialogs:SaveGroup:Impact:Actions:ConfirmSave").Click();

        var result = await dialog.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        var applyCall = applyService.ReceivedCalls().Single();
        var request = (ConfigurationMutationGroupApplyRequest)applyCall.GetArguments()[0]!;
        request.Commands.Should().ContainSingle(command => command.DefinitionKey == "Sample.Options");
    }

    [Fact]
    public async Task Save_WhenReviewWasUndone_ShouldAnalyzeOnlyExactRemainingParameterTargets()
    {
        var impactService = CreateSuccessfulImpactService();
        var applyService = CreateSuccessfulApplyService();
        await using var context = new ConfigurationUiTestContext(
            CreateFacade(impactService, applyService),
            new ModuleConfigurationUIOption { EnableAffectedServiceConfirmation = true });
        var (surface, dialog) = await ShowSaveDialogAsync(
            context,
            [
                CreatePendingChange("Alpha.Options", "Alpha options", "AlphaName"),
                CreatePendingChange("Zulu.Options", "Zulu options", "ZuluName")
            ],
            enableUndo: true);

        var undoButtons = surface.FindAll("button")
            .Where(button => button.GetAttribute("aria-label")?.Contains(
                "PendingReview:Actions:UndoDefinition",
                StringComparison.Ordinal) is true)
            .ToArray();
        undoButtons.Should().HaveCount(2);
        undoButtons[0].Click();
        surface.WaitForAssertion(() =>
            FindButton(surface, "Group:SaveCount").TextContent.Should().Contain("[1]"));

        FindButton(surface, "Group:SaveCount").Click();
        surface.WaitForAssertion(() =>
            FindButton(surface, "Dialogs:SaveGroup:Impact:Actions:ConfirmSave").Should().NotBeNull());

        var impactCall = impactService.ReceivedCalls().Single();
        var targets = (IReadOnlyCollection<ConfigurationParameterChangeTarget>)impactCall.GetArguments()[0]!;
        targets.Should().ContainSingle();
        targets.Single().DefinitionKey.Should().Be("Zulu.Options");
        targets.Single().LogicalPath.ToCanonicalString().Should().Be("ZuluName");

        FindLastDialogButton(surface, "Common:Actions:Cancel").Click();
        surface.WaitForAssertion(() =>
            surface.Markup.Should().NotContain("Dialogs:SaveGroup:Impact:Actions:ConfirmSave"));
        FindButton(surface, "Common:Actions:Cancel").Click();
        (await dialog.Result)!.Canceled.Should().BeTrue();
    }

    [Fact]
    public async Task Save_WhenImpactAnalysisFails_ShouldApplyOnlyAfterSaveAnyway()
    {
        var impactService = Substitute.For<IConfigurationDefinitionChangeImpactService>();
        impactService.GetImpactAsync(
                Arg.Any<IReadOnlyCollection<ConfigurationParameterChangeTarget>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ConfigurationDefinitionChangeImpact>(
                new InvalidOperationException("publisher metadata failed")));
        var applyService = CreateSuccessfulApplyService();
        await using var context = new ConfigurationUiTestContext(
            CreateFacade(impactService, applyService),
            new ModuleConfigurationUIOption { EnableAffectedServiceConfirmation = true });
        var (surface, dialog) = await ShowSaveDialogAsync(context);

        FindButton(surface, "Group:SaveCount").Click();
        surface.WaitForAssertion(() =>
            FindButton(surface, "Dialogs:SaveGroup:Impact:Actions:SaveAnyway").Should().NotBeNull());
        surface.Markup.Should().Contain("publisher metadata failed");
        applyService.ReceivedCalls().Should().BeEmpty();

        FindButton(surface, "Dialogs:SaveGroup:Impact:Actions:SaveAnyway").Click();

        var result = await dialog.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        applyService.ReceivedCalls().Should().ContainSingle();
    }

    private static IConfigurationDefinitionChangeImpactService CreateSuccessfulImpactService()
    {
        var impactService = Substitute.For<IConfigurationDefinitionChangeImpactService>();
        impactService.GetImpactAsync(
                Arg.Any<IReadOnlyCollection<ConfigurationParameterChangeTarget>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ConfigurationDefinitionChangeImpact
            {
                AffectedPublishers =
                [
                    new ConfigurationAffectedPublisher
                    {
                        PublisherKey = "sample-service",
                        Parameters =
                        [
                            new ConfigurationAffectedParameter
                            {
                                DefinitionKey = "Sample.Options",
                                DefinitionDisplayName = "Sample options",
                                LogicalPath = LogicalPath.FromProperties("Name"),
                                ParameterDisplayName = "Name",
                                ObservationKind = ConfigurationReloadBehaviorObservationKind.Inferred,
                                ReloadBehavior = ConfigurationReloadBehavior.OnlineReloadable
                            }
                        ]
                    }
                ],
                ParametersWithoutKnownConsumers = []
            });
        return impactService;
    }

    private static IConfigurationMutationGroupApplyService CreateSuccessfulApplyService()
    {
        var applyService = Substitute.For<IConfigurationMutationGroupApplyService>();
        applyService.ApplyAsync(
                Arg.Any<ConfigurationMutationGroupApplyRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new ConfigurationMutationGroupApplyResult
            {
                Status = ConfigurationMutationGroupApplyStatus.Applied,
                MutationGroup = new ConfigurationMutationGroup
                {
                    GroupId = "group-1",
                    Label = call.Arg<ConfigurationMutationGroupApplyRequest>().Label,
                    DefinitionKeys = ["Sample.Options"],
                    MutationCount = 1,
                    CreatedTime = DateTimeOffset.UtcNow
                }
            });
        return applyService;
    }

    private static ConfigurationFacade CreateFacade(
        IConfigurationDefinitionChangeImpactService impactService,
        IConfigurationMutationGroupApplyService applyService)
    {
        return new ConfigurationFacade(
            definitionResolver: null!,
            definitionRegistry: null!,
            definitionMaintenanceStore: null!,
            definitionChangeImpactService: impactService,
            mutationGroupApplyService: applyService,
            historyService: null!,
            mutationGroupService: null!,
            rollbackService: null!,
            unifiedVersionService: null!,
            effectiveValueStore: null!,
            historyStore: null!,
            metadataStore: null!,
            changeNotifiers: [],
            storeStateTracker: null!,
            effectiveStateReader: null!,
            sourceInspector: null!,
            sourceWriter: null!,
            runtimeContext: null!,
            runtimeValidationService: null!,
            optionsValidationDiagnostics: Substitute.For<IConfigurationOptionsValidationDiagnostics>(),
            candidateValidationService: null!,
            runtimeReloadService: null!,
            reloadBroadcastService: null!);
    }

    private static async Task<(IRenderedComponent<MudDialogProvider> Surface, IDialogReference Dialog)>
        ShowSaveDialogAsync(
            ConfigurationUiTestContext context,
            IReadOnlyList<PendingChange>? changes = null,
            bool enableUndo = false)
    {
        var parameters = new DialogParameters<SaveMutationGroupDialog>();
        parameters.Add(component => component.Changes, changes ?? [CreatePendingChange()]);
        parameters.Add(component => component.ValidationIssues, Array.Empty<ConfigurationValidationIssue>());
        if (enableUndo)
        {
            parameters.Add(
                component => component.UndoRequested,
                EventCallback.Factory.Create<ConfigurationPendingReviewUndoRequest>(
                    context,
                    static _ => Task.CompletedTask));
        }

        var dialog = await context.Services.GetRequiredService<IDialogService>()
            .ShowAsync<SaveMutationGroupDialog>("Save group", parameters);
        return (context.DialogProvider, dialog);
    }

    private static PendingChange CreatePendingChange(
        string definitionKey = "Sample.Options",
        string definitionDisplayName = "Sample options",
        string propertyName = "Name")
    {
        return new PendingChange
        {
            DefinitionKey = definitionKey,
            DefinitionDisplayName = definitionDisplayName,
            LogicalPath = LogicalPath.FromProperties(propertyName),
            NodeDisplayName = propertyName,
            MutationKind = ConfigurationMutationKind.Set,
            NewValue = ConfigurationStoredValue.FromJson("\"new value\""),
            OriginalValue = ConfigurationStoredValue.FromJson("\"old value\""),
            OriginalDisplayValue = "old value",
            NewDisplayValue = "new value",
            ExpectedSchemaVersion = 1,
            ExpectedValueVersion = 1,
            NodeKind = ConfigurationNodeKind.Scalar,
            ValueKind = ConfigurationValueKind.String,
            ReloadBehavior = ConfigurationReloadBehavior.OnlineReloadable
        };
    }

    private static AngleSharp.Dom.IElement FindButton(
        IRenderedComponent<MudDialogProvider> surface,
        string text)
    {
        return surface.FindAll("button")
            .Single(button => button.TextContent.Contains(text, StringComparison.Ordinal));
    }

    private static AngleSharp.Dom.IElement FindLastDialogButton(
        IRenderedComponent<MudDialogProvider> surface,
        string text)
    {
        return surface.FindAll(".mud-dialog")
            .Last()
            .QuerySelectorAll("button")
            .Single(button => button.TextContent.Contains(text, StringComparison.Ordinal));
    }
}
