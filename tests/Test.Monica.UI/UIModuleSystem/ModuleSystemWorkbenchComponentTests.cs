using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Modules;
using Monica.UI.Localization;
using Monica.UI.Pages;
using Monica.UI.Shell.Support;
using Monica.UI.UIModuleSystem.Components;
using Monica.UI.UIModuleSystem.State;
using Monica.UI.UIModuleSystem.Support;
using MudBlazor;
using Test.Monica.UI.Shell.Support;
using Xunit;

namespace Test.Monica.UI.UIModuleSystem;

public sealed class ModuleSystemWorkbenchComponentTests
{
    [Theory]
    [InlineData("en-US", "Declare service contracts")]
    [InlineData("zh-CN", "声明服务契约")]
    public void DeclareContracts_phase_uses_the_real_bilingual_resource(
        string cultureName,
        string expectedLabel)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddLocalization().AddResource<ModuleSystemResource>();
        });
        using var localizationHost = builder.Build();
        var localizer = localizationHost.Services
            .GetRequiredService<IStringLocalizer<ModuleSystemResource>>();
        using var culture = new CultureScope(cultureName);

        ModuleDiagnosticsDisplay.Phase(ModulePhase.DeclareContracts, localizer)
            .Should().Be(expectedLabel);
    }

    [Fact]
    public async Task Unauthorized_page_never_invokes_the_diagnostics_boundary()
    {
        var facadeCalls = 0;
        await using var context = new ModuleSystemWorkbenchUiTestContext(configureServices: services =>
        {
            AddOperationalAccess(services, Environments.Production);
            services.AddSingleton(new ModuleSystemWorkbenchSessionFactory(
                ModuleSystemWorkbenchTestData.Calls(snapshot: () =>
                {
                    facadeCalls++;
                    return Res.Ok(ModuleSystemWorkbenchTestData.Snapshot());
                })));
        });

        var cut = context.Render<ModuleSystemPage>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Access:DeniedTitle"));
        facadeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Module_query_exists_only_while_the_explicit_module_drawer_is_open()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext(configureServices: services =>
        {
            AddOperationalAccess(services, Environments.Development);
            services.AddSingleton(new ModuleSystemWorkbenchSessionFactory(
                ModuleSystemWorkbenchTestData.Calls()));
        });

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(
            $"/module-system-dashboard/modules?module={Uri.EscapeDataString(ModuleSystemWorkbenchTestData.BetaKey.Id)}");
        var cut = context.Render<ModuleSystemPage>(parameters => parameters
            .Add(component => component.Section, "modules"));
        cut.WaitForAssertion(() => cut.FindComponent<ModuleWorkbenchModuleDrawer>()
            .Instance.Session.IsModuleDrawerOpen.Should().BeTrue());
        var session = cut.FindComponent<ModuleWorkbenchModuleDrawer>().Instance.Session;
        cut.Find("button[aria-label='Aria:CloseModuleDrawer']").Click();

        cut.WaitForAssertion(() =>
        {
            session.IsModuleDrawerOpen.Should().BeFalse();
            new Uri(navigation.Uri).PathAndQuery.Should().Be("/module-system-dashboard/modules");
        });

        cut.FindAll(".workbench-desktop-sections button")[1].Click();

        new Uri(navigation.Uri).PathAndQuery.Should().Be("/module-system-dashboard/performance");
    }

    [Fact]
    public async Task Root_route_keeps_the_automatic_workbench_selection_internal_and_the_url_clean()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext(configureServices: services =>
        {
            AddOperationalAccess(services, Environments.Development);
            services.AddSingleton(new ModuleSystemWorkbenchSessionFactory(
                ModuleSystemWorkbenchTestData.Calls()));
        });

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/module-system-dashboard");
        var cut = context.Render<ModuleSystemPage>();

        cut.WaitForAssertion(() =>
        {
            var drawer = cut.FindComponent<ModuleWorkbenchModuleDrawer>();
            drawer.Instance.Session.SelectedModule.Should().NotBeNull();
            drawer.Instance.Session.IsModuleDrawerOpen.Should().BeFalse();
            new Uri(navigation.Uri).PathAndQuery.Should().Be("/module-system-dashboard");
        });
    }

    [Fact]
    public async Task Initial_and_failed_states_have_distinct_observable_ui()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var initial = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        var loading = context.Render<ModuleWorkbenchLoadState>(parameters => parameters
            .Add(component => component.Session, initial));
        loading.Markup.Should().Contain("Load:Loading");

        await using var failed = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        var request = failed.BeginSnapshotRequest(isInitial: true);
        failed.CompleteSnapshotRequest(request, Res.Fail("offline"));
        var error = context.Render<ModuleWorkbenchLoadState>(parameters => parameters
            .Add(component => component.Session, failed));

        error.Markup.Should().Contain("Load:FailedTitle");
        error.Markup.Should().Contain("Actions:Retry");
    }

    [Fact]
    public async Task Refresh_failure_remains_visible_above_every_workbench_section()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        var request = session.BeginSnapshotRequest(isInitial: false);
        session.CompleteSnapshotRequest(request, Res.Fail("offline"));

        var cut = context.Render<ModuleWorkbenchHeader>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain("Load:RefreshFailed");
        cut.Markup.Should().Contain("Actions:Retry");
    }

    [Fact]
    public async Task Module_catalog_renders_the_session_filter_projection()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        session.SetModuleFilters(
            "Beta",
            ModuleCatalogStateFilter.Active,
            ModuleCatalogCapabilityFilter.Web,
            assembly: null,
            onlyWithDependencies: false,
            minimumCostMs: 0);

        var cut = context.Render<ModuleWorkbenchModules>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain("BetaModule");
        cut.Markup.Should().NotContain("AlphaModule");
    }

    [Fact]
    public async Task Overview_metrics_render_as_one_contiguous_fact_grid()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();

        var cut = context.Render<ModuleWorkbenchOverview>(parameters => parameters
            .Add(component => component.Session, session));

        var facts = cut.FindAll(".mo-vl-facts > .mo-vl-fact");

        facts.Should().HaveCount(4);
        facts.Should().AllSatisfy(fact => fact.QuerySelector(".mo-card-surface").Should().BeNull());
        cut.Find(".overview-score .startup-timeline").Should().NotBeNull();
    }

    [Fact]
    public async Task Module_catalog_renders_truncated_identifiers_without_clipboard_actions()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();

        var cut = context.Render<ModuleWorkbenchModules>(parameters => parameters
            .Add(component => component.Session, session));

        cut.FindAll(".identifier-value").Should().HaveCount(session.PagedModules.Count);
        cut.FindAll(".copyable-identifier").Should().BeEmpty();
        cut.FindAll("button[aria-label^='Aria:Copy']").Should().BeEmpty();
    }

    [Fact]
    public async Task Discovery_loads_the_assembly_explorer_and_defaults_to_attention_when_issues_exist()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var inventoryCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls(
            inventory: () =>
            {
                inventoryCalls++;
                return Res.Ok(ModuleSystemWorkbenchTestData.Inventory());
            }));
        await session.InitializeAsync();

        var cut = context.Render<ModuleWorkbenchDiscovery>(parameters => parameters
            .Add(component => component.Session, session));
        cut.WaitForAssertion(() =>
        {
            session.AssemblyInventoryState.Should().Be(ModuleSystemLazyLoadState.Ready);
            session.AssemblyFacet.Should().Be(AssemblyInventoryFacet.Attention);
            cut.Markup.Should().Contain("Failed.Assembly");
            cut.Markup.Should().Contain("Scanned.Assembly");
            cut.Markup.Should().Contain("Test.*");

        });

        inventoryCalls.Should().Be(1);
        session.FilteredAssemblies.Should().ContainSingle(record =>
            record.Outcome == TypeDiscoveryAssemblyOutcome.ResolutionFailed);
    }

    [Fact]
    public async Task Selected_module_drawer_renders_the_synchronized_selection()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        session.SelectModule(session.Snapshot!.Modules[1]);

        var cut = context.Render<ModuleWorkbenchModuleDrawer>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain("BetaModule");
        cut.Markup.Should().Contain("ModuleDrawer:Tabs:Summary");
        cut.Markup.Should().Contain("ModuleDrawer:Tabs:Configuration");
        session.IsModuleDrawerOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Configuration_projection_renders_nested_redacted_values_without_copy_affordances()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var diagnostics = ModuleSystemWorkbenchTestData.Options(ModuleSystemWorkbenchTestData.AlphaKey);

        var cut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, diagnostics));

        cut.Markup.Should().Contain("ModuleDrawer:Options:RedactedPolicyTitle");
        cut.Markup.Should().Contain("Transport");
        cut.Markup.Should().Contain("RetryDelays");
        cut.Markup.Should().Contain("ApiToken");
        cut.Markup.Should().Contain("ModuleDrawer:Options:SensitiveProtected");
        cut.Markup.Should().NotContain("ModuleDrawer:Options:ExcludedCount");
        cut.Markup.Should().NotContain("debug-secret-value");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public async Task Configuration_projection_in_sensitive_debug_mode_renders_a_strong_warning_and_revealed_value()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var diagnostics = ModuleSystemWorkbenchTestData.Options(
            ModuleSystemWorkbenchTestData.AlphaKey,
            ModuleOptionDiagnosticsExposureMode.RevealSensitive);

        var cut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, diagnostics));

        cut.Markup.Should().Contain("ModuleDrawer:Options:RevealPolicyTitle");
        cut.Markup.Should().Contain("ModuleDrawer:Options:SensitiveRevealed");
        cut.Markup.Should().Contain("debug-secret-value");
        cut.Find(".option-entry--revealed-sensitive").Should().NotBeNull();
    }

    [Fact]
    public async Task Configuration_projection_in_sensitive_debug_mode_does_not_mark_an_empty_secret_as_visible()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var diagnostics = ModuleSystemWorkbenchTestData.Options(
            ModuleSystemWorkbenchTestData.AlphaKey,
            ModuleOptionDiagnosticsExposureMode.RevealSensitive) with
        {
            ContainsRevealedSensitiveValues = false,
            Entries =
            [
                new ModuleOptionDiagnosticEntry
                {
                    Name = "ApiToken",
                    Path = "ApiToken",
                    TypeName = "System.String",
                    Kind = ModuleOptionDiagnosticValueKind.Value,
                    Value = string.Empty,
                    IsSensitive = true
                }
            ]
        };

        var cut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, diagnostics));

        cut.FindAll(".option-entry--revealed-sensitive").Should().BeEmpty();
        cut.Markup.Should().Contain("ModuleDrawer:Options:SensitiveProtected");
    }

    [Fact]
    public async Task Configuration_projection_in_sensitive_debug_mode_marks_nested_groups_as_visible()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var diagnostics = ModuleSystemWorkbenchTestData.Options(
            ModuleSystemWorkbenchTestData.AlphaKey,
            ModuleOptionDiagnosticsExposureMode.RevealSensitive) with
        {
            Entries =
            [
                new ModuleOptionDiagnosticEntry
                {
                    Name = "ApiTokens",
                    Path = "ApiTokens",
                    TypeName = "List<ApiTokenOptions>",
                    Kind = ModuleOptionDiagnosticValueKind.Collection,
                    IsSensitive = true,
                    Count = 1,
                    Children =
                    [
                        new ModuleOptionDiagnosticEntry
                        {
                            Name = "[0]",
                            Path = "ApiTokens[0]",
                            TypeName = "ApiTokenOptions",
                            Kind = ModuleOptionDiagnosticValueKind.Object,
                            IsSensitive = true,
                            Children =
                            [
                                new ModuleOptionDiagnosticEntry
                                {
                                    Name = "Value",
                                    Path = "ApiTokens[0].Value",
                                    TypeName = "String",
                                    Kind = ModuleOptionDiagnosticValueKind.Value,
                                    Value = "nested-debug-secret",
                                    IsSensitive = true
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var cut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, diagnostics));

        var groups = cut.FindAll("details.option-entry--revealed-sensitive");
        groups.Should().HaveCount(2);
        foreach (var group in groups)
        {
            group.QuerySelector(":scope > summary")!.TextContent
                .Should().Contain("ModuleDrawer:Options:SensitiveRevealed")
                .And.NotContain("ModuleDrawer:Options:SensitiveProtected");
            group.QuerySelector(":scope > summary .mud-chip")!.ClassList
                .Should().Contain("mud-chip-color-error");
        }

        cut.Markup.Should().Contain("nested-debug-secret");
    }

    [Fact]
    public async Task Configuration_projection_WhenTheGlobalBoundIsReached_ShouldShowTheTruncationState()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var diagnostics = ModuleSystemWorkbenchTestData.Options(ModuleSystemWorkbenchTestData.AlphaKey) with
        {
            IsTruncated = true
        };

        var cut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, diagnostics));

        cut.Markup.Should().Contain("ModuleDrawer:Options:ProjectionTruncated");
    }

    [Fact]
    public async Task Configuration_projection_WhenAGroupIsBounded_ShouldMarkTheEntryAsPartial()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var diagnostics = ModuleSystemWorkbenchTestData.Options(ModuleSystemWorkbenchTestData.AlphaKey) with
        {
            Entries =
            [
                new ModuleOptionDiagnosticEntry
                {
                    Name = "NestedOptions",
                    Path = "NestedOptions",
                    TypeName = "NestedOptions",
                    Kind = ModuleOptionDiagnosticValueKind.Object,
                    IsTruncated = true
                }
            ]
        };

        var cut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, diagnostics));

        cut.Markup.Should().Contain("ModuleDrawer:Options:EntryTruncated");
    }

    [Fact]
    public async Task Configuration_projection_distinguishes_unfinalized_from_empty_configuration()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var unfinalized = ModuleSystemWorkbenchTestData.Options(ModuleSystemWorkbenchTestData.AlphaKey) with
        {
            IsFinalized = false,
            Entries = []
        };
        var empty = unfinalized with { IsFinalized = true };

        var pendingCut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, unfinalized));
        pendingCut.Markup.Should().Contain("ModuleDrawer:Options:UnfinalizedTitle");
        pendingCut.Markup.Should().NotContain("ModuleDrawer:Options:EmptyTitle");

        var emptyCut = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, empty));
        emptyCut.Markup.Should().Contain("ModuleDrawer:Options:EmptyTitle");
        emptyCut.Markup.Should().NotContain("ModuleDrawer:Options:UnfinalizedTitle");
    }

    [Theory]
    [InlineData("en-US", "Recorded 12.0 ms; configured budget 8.00 ms.")]
    [InlineData("zh-CN", "记录 12.0 ms，配置预算 8.00 ms。")]
    public async Task Structured_finding_code_is_rendered_from_the_real_bilingual_resources(
        string cultureName,
        string expectedMessage)
    {
        var finding = new ModuleDiagnosticFinding
        {
            Code = ModuleDiagnosticFindingCodes.PERFORMANCE_BUDGET_EXCEEDED,
            Severity = ModuleDiagnosticFindingSeverity.Warning,
            Evidence = new ModuleDiagnosticFindingEvidence
            {
                Kind = ModuleDiagnosticFindingEvidenceKind.PerformanceBudget,
                PerformanceMetric = ModulePerformanceBudgetKind.TypeDiscovery,
                ActualDurationMs = 12,
                LimitDurationMs = 8
            }
        };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddLocalization().AddResource<ModuleSystemResource>();
        });
        using var localizationHost = builder.Build();
        var localizer = localizationHost.Services
            .GetRequiredService<IStringLocalizer<ModuleSystemResource>>();
        using var culture = new CultureScope(cultureName);
        await using var context = new ModuleSystemWorkbenchUiTestContext(localizer);
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls(
            snapshot: () => Res.Ok(ModuleSystemWorkbenchTestData.Snapshot(findings: [finding]))));
        await session.InitializeAsync();

        var cut = context.Render<ModuleWorkbenchOverview>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Markup.Should().Contain(expectedMessage);
        cut.Markup.Should().NotContain("Findings:PerformanceBudgetExceeded");
        cut.Markup.Should().Contain(ModuleDiagnosticFindingCodes.PERFORMANCE_BUDGET_EXCEEDED);
    }

    [Fact]
    public async Task Overview_WithoutStartupMarker_ShouldUseCompositionSpectrumWithUntrackedNote()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var snapshot = ModuleSystemWorkbenchTestData.Snapshot(applicationStartupDurationMs: null);
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(snapshot: () => Res.Ok(snapshot)));
        await session.InitializeAsync();

        var cut = context.Render<ModuleWorkbenchOverview>(parameters => parameters
            .Add(component => component.Session, session));

        cut.FindAll(".score-note").Should().ContainSingle();
        cut.Markup.Should().NotContain("Overview:Score:Eyebrow:Startup");
        cut.Find(".mo-vl-display__value").TextContent.Trim().Should().Be("24.0");
        cut.Find(".mo-vl-display__unit").TextContent.Trim().Should().Be("ms");
        cut.Find(".score-exact").TextContent.Trim().Should().Be("24.0000 ms");
        cut.FindAll(".mo-vl-fact").Should().HaveCount(4);
    }

    [Fact]
    public async Task Overview_WithTrackedBaseline_ShouldRenderExactApplicationStartupDelta()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        await ImportBaselineAsync(session, ModuleSystemWorkbenchTestData.Export(applicationStartupDurationMs: 30));

        var cut = context.Render<ModuleWorkbenchOverview>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find(".score-delta").TextContent.Should().Contain("6.1250 ms");
    }

    [Theory]
    [InlineData(null, 30d)]
    [InlineData(36.125d, null)]
    public async Task Overview_WhenEitherSnapshotIsUntracked_ShouldOmitApplicationStartupDelta(
        double? currentStartupDurationMs,
        double? baselineStartupDurationMs)
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        var snapshot = ModuleSystemWorkbenchTestData.Snapshot(
            applicationStartupDurationMs: currentStartupDurationMs);
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(snapshot: () => Res.Ok(snapshot)));
        await session.InitializeAsync();
        await ImportBaselineAsync(
            session,
            ModuleSystemWorkbenchTestData.Export(
                applicationStartupDurationMs: baselineStartupDurationMs));

        var cut = context.Render<ModuleWorkbenchOverview>(parameters => parameters
            .Add(component => component.Session, session));

        cut.FindAll(".score-delta").Should().BeEmpty();
    }

    [Fact]
    public async Task CriticalPath_WhenChainExceedsTenSpans_ShouldRenderCompleteOrderAndHighlightSelection()
    {
        var workSpans = Enumerable.Range(0, 12)
            .Select(index => new ModuleDiagnosticsTraceSpan
            {
                SpanId = $"work-span-{index:D2}",
                Kind = ModuleDiagnosticsTraceSpanKind.StartupWork,
                ModuleKey = ModuleSystemWorkbenchTestData.BetaKey,
                WorkItemId = $"work-{index:D2}",
                Name = $"work-{index:D2}",
                StartedOffsetMs = index,
                EndedOffsetMs = index + 1
            })
            .ToArray();
        var barrierSpan = new ModuleDiagnosticsTraceSpan
        {
            SpanId = "barrier-span",
            Kind = ModuleDiagnosticsTraceSpanKind.StartupBarrier,
            Barrier = ModuleStartupWorkBarrier.BeforePostConfigureServices,
            StartedOffsetMs = 12,
            EndedOffsetMs = 13
        };
        var unrelatedSpan = new ModuleDiagnosticsTraceSpan
        {
            SpanId = "unrelated-span",
            Kind = ModuleDiagnosticsTraceSpanKind.SystemStage,
            StartedOffsetMs = 13,
            EndedOffsetMs = 14
        };
        var blockingChain = workSpans.Select((span, index) => new ModuleBlockingChainSegment
        {
            SegmentId = $"segment-{index:D2}",
            BarrierSpanId = barrierSpan.SpanId,
            WorkItemId = span.WorkItemId!,
            WorkSpanId = span.SpanId,
            ModuleKey = ModuleSystemWorkbenchTestData.BetaKey,
            Barrier = ModuleStartupWorkBarrier.BeforePostConfigureServices,
            BlockingDurationMs = 12 - index,
            IsBarrierReleaser = index == workSpans.Length - 1
        }).ToImmutableArray();
        var snapshot = ModuleSystemWorkbenchTestData.Snapshot() with
        {
            TraceSpans = [.. workSpans, barrierSpan, unrelatedSpan],
            BlockingChain = blockingChain
        };
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(snapshot: () => Res.Ok(snapshot)));
        await session.InitializeAsync();

        var ribbon = context.Render<ModuleWorkbenchCriticalPathRibbon>(parameters => parameters
            .Add(component => component.Session, session));
        var buttons = ribbon.FindAll("button.ribbon-segment");

        buttons.Select(button => button.GetAttribute("data-span-id"))
            .Should().Equal(workSpans.Select(static span => span.SpanId).Append(barrierSpan.SpanId));
        buttons.Should().HaveCount(13);
        ribbon.Markup.Should().NotContain(unrelatedSpan.SpanId);

        buttons[11].Click();
        session.SelectedTraceSpanId.Should().Be(workSpans[11].SpanId);

        var performance = context.Render<ModuleWorkbenchPerformance>(parameters => parameters
            .Add(component => component.Session, session));
        performance.Find($"rect.timeline-bar-selected[data-span-id='{workSpans[11].SpanId}']")
            .Should().NotBeNull();
        performance.Find($".timeline-value[data-span-id='{workSpans[11].SpanId}']")
            .GetAttribute("aria-current").Should().Be("true");
        performance.FindAll("tr.timeline-row-selected").Should().ContainSingle();
    }

    [Fact]
    public async Task Waterfall_interval_can_drive_selection_and_focus_without_losing_exact_timing()
    {
        await using var context = new ModuleSystemWorkbenchUiTestContext();
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        var cut = context.Render<ModuleCompositionWaterfall>(parameters => parameters
            .Add(component => component.Session, session));

        cut.Find("button.timeline-waterfall__track-button[data-span-id='span-alpha']").Click();

        session.SelectedTraceSpanId.Should().Be("span-alpha");
        session.SelectedModule!.TypeName.Should().Be("AlphaModule");
        cut.Find(".timeline-waterfall__selection").TextContent.Should().Contain("AlphaModule");

        var waterfall = cut.Find("section.timeline-waterfall");
        ParseSvgNumber(waterfall.GetAttribute("data-axis-width")).Should().BeApproximately(24, .001);
        cut.Find("button[aria-label='Performance:Waterfall:FocusSelected']").Click();

        cut.Find("section.timeline-waterfall").GetAttribute("data-zoom-level").Should().Be("2");
        ParseSvgNumber(cut.Find("section.timeline-waterfall").GetAttribute("data-axis-width"))
            .Should().BeApproximately(12, .001);
        cut.Find("rect.timeline-bar-selected[data-span-id='span-alpha']").Should().NotBeNull();
    }

    private static void AddOperationalAccess(IServiceCollection services, string environmentName)
    {
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddSingleton<IOptions<ModuleShellUIOption>>(Options.Create(new ModuleShellUIOption()));
        services.AddSingleton<IOptions<ModuleSystemUIOption>>(Options.Create(new ModuleSystemUIOption()));
        services.AddSingleton<OperationalPageAccessEvaluator>();
        services.AddSingleton<OperationalPageAccessPolicy<ModuleSystemUIOption>>();
    }

    private static double ParseSvgNumber(string? value) =>
        double.Parse(value!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static async Task ImportBaselineAsync(
        ModuleSystemWorkbenchSession session,
        ModuleDiagnosticsExport export)
    {
        var json = JsonSerializer.Serialize(export);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await session.ImportBaselineAsync(stream, Xunit.TestContext.Current.CancellationToken);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        internal CultureScope(string cultureName)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}
