using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.Framework.Seeder.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UISeeder.Components;
using Monica.Framework.UI.UISeeder.State;
using Monica.Testing.Localization;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace Test.Monica.Framework.UI.Seeder;

public sealed class SeederOperationsComponentTests
{
    [Fact]
    public async Task RunOverview_ShouldRenderRunAndReadinessStatuses()
    {
        var optionalFailure = SeederUiTestData.CreateSeeder(
            status: SeederStatus.Failed,
            criticality: SeederCriticality.Optional);
        await using var session = await CreateReadySessionAsync(SeederUiTestData.CreateSnapshot(
            SeederRunStatus.CompletedWithFailures,
            [optionalFailure]));
        await using var context = CreateContext();

        var cut = context.Render<SeederRunOverview>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain("RunStatus:CompletedWithFailures");
        cut.Markup.Should().Contain("ReadinessStatus:Degraded");
        var labelIds = cut.Find("[role='progressbar']").GetAttribute("aria-labelledby")!
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        labelIds.Should().NotBeEmpty();
        foreach (var id in labelIds)
        {
            cut.Find($"[id='{id}']").TextContent.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task ConfigurationPanel_ShouldRenderEffectiveModuleDefaults()
    {
        await using var session = await CreateReadySessionAsync(SeederUiTestData.CreateSnapshot());
        await using var context = CreateContext();

        var cut = context.Render<SeederConfigurationPanel>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain("Configuration:MaxConcurrency");
        cut.Markup.Should().Contain("4");
        cut.Markup.Should().Contain("ExecutionMode:Concurrent");
        cut.Markup.Should().Contain("FailureBehavior:ContinueAndRecord");
    }

    [Fact]
    public async Task ExecutionTable_ShouldApplySessionFiltersAndRenderEmptyState()
    {
        var succeeded = SeederUiTestData.CreateSeeder("CatalogSeeder");
        var failed = SeederUiTestData.CreateSeeder(
            "SearchIndexSeeder",
            SeederStatus.Failed,
            SeederCriticality.Optional,
            SeederFailureBehavior.FailFast,
            errorMessage: "Unavailable");
        await using var session = await CreateReadySessionAsync(SeederUiTestData.CreateSnapshot(
            SeederRunStatus.CompletedWithFailures,
            [succeeded, failed]));
        await using var context = CreateContext();

        await session.SetSearchTextAsync("SearchIndex");
        var filtered = context.Render<SeederExecutionTable>(parameters => parameters
            .Add(component => component.Session, session));

        filtered.Markup.Should().Contain("SearchIndexSeeder");
        filtered.Markup.Should().NotContain("CatalogSeeder");
        var detailsButton = filtered.Find("button[aria-haspopup='dialog']");
        detailsButton.GetAttribute("aria-controls").Should().Be("seeder-detail-dialog");
        detailsButton.GetAttribute("aria-expanded").Should().Be("false");

        await session.SetSearchTextAsync("not-present");
        var empty = context.Render<SeederExecutionTable>(parameters => parameters
            .Add(component => component.Session, session));
        empty.Find("[data-testid='seeder-table-empty']").TextContent.Should().Contain("Table:Empty");
        empty.Markup.Should().NotContain("Table:NoSeeders");
    }

    [Fact]
    public async Task ExecutionTable_WhenGraphIsEmpty_ShouldExplainThatNoSeedersWereDiscovered()
    {
        await using var session = await CreateReadySessionAsync(SeederUiTestData.CreateSnapshot(
            SeederRunStatus.Succeeded,
            []));
        await using var context = CreateContext();

        var cut = context.Render<SeederExecutionTable>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find("[data-testid='seeder-table-empty']").TextContent.Should().Contain("Table:NoSeeders");
        cut.Markup.Should().NotContain("Table:EmptyDescription");
    }

    [Fact]
    public async Task DetailsDrawer_ShouldRenderPolicyOriginDependenciesAttemptsAndBoundedError()
    {
        var dependency = SeederUiTestData.CreateSeeder("CatalogSeeder");
        var seeder = SeederUiTestData.CreateSeeder(
            "SearchIndexSeeder",
            SeederStatus.Failed,
            SeederCriticality.Optional,
            SeederFailureBehavior.FailFast,
            ["Example.Seeders.CatalogSeeder"],
            "Search service unavailable");
        await using var session = await CreateReadySessionAsync(SeederUiTestData.CreateSnapshot(
            SeederRunStatus.Aborted,
            [dependency, seeder]));
        await session.OpenDetailsAsync(seeder);
        await using var context = CreateContext();

        var cut = context.Render<SeederDetailsDrawer>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain("PolicySource:SeederOverride");
        cut.Markup.Should().Contain("Example.Seeders.CatalogSeeder");
        cut.Markup.Should().Contain("SeederStatus:Succeeded");
        var attempt = cut.FindAll("[data-testid='seeder-attempt-row']").Should().ContainSingle().Which;
        attempt.TextContent.Should().Contain("Details:Timing:CompletedAt");
        cut.Markup.Should().Contain("System.InvalidOperationException");
        cut.Markup.Should().Contain("Search service unavailable");
        var dialog = cut.Find("[role='dialog']");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        dialog.GetAttribute("aria-labelledby").Should().Be("seeder-detail-title");
        var scrollRegion = cut.Find(".seeder-detail-content");
        scrollRegion.GetAttribute("role").Should().Be("region");
        scrollRegion.GetAttribute("aria-labelledby").Should().Be("seeder-detail-title");
        scrollRegion.GetAttribute("tabindex").Should().Be("0");
        cut.Markup.Should().Contain("mud-focus-trap");
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        context.Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        context.Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        context.Services.AddSingleton<IStringLocalizer<SeederResource>, EchoStringLocalizer<SeederResource>>();
        _ = context.Render<MudPopoverProvider>();
        return context;
    }

    private static async Task<SeederPageSession> CreateReadySessionAsync(SeederDiagnosticsSnapshot snapshot)
    {
        var session = new SeederPageSession(
            _ => Task.FromResult(Res.Ok(snapshot)),
            _ => Task.FromResult(true),
            "test-host",
            refreshInterval: TimeSpan.FromHours(1));
        await session.InitializeAsync();
        return session;
    }
}
