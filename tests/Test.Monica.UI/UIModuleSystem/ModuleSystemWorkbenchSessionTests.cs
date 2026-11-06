using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.UI.UIModuleSystem.State;
using Monica.UI.UIModuleSystem.Support;
using Xunit;

namespace Test.Monica.UI.UIModuleSystem;

public sealed class ModuleSystemWorkbenchSessionTests
{
    [Fact]
    public async Task Refresh_failure_preserves_the_previous_immutable_snapshot()
    {
        var first = ModuleSystemWorkbenchTestData.Snapshot(revision: 7);
        var calls = 0;
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(snapshot: () => ++calls == 1
                ? Result.Success(first)
                : Result.Failure<ModuleDiagnosticsSnapshot>("refresh failed")));

        await session.InitializeAsync();
        await session.RefreshAsync();

        session.Snapshot.Should().BeSameAs(first);
        session.LoadState.Should().Be(ModuleSystemWorkbenchLoadState.Ready);
        session.RefreshError.Should().Be("refresh failed");
    }

    [Fact]
    public async Task Older_snapshot_completion_cannot_replace_a_newer_request()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        var older = session.BeginSnapshotRequest(isInitial: true);
        var newer = session.BeginSnapshotRequest(isInitial: true);

        session.CompleteSnapshotRequest(older, Result.Success(ModuleSystemWorkbenchTestData.Snapshot(1)))
            .Should().BeFalse();
        session.CompleteSnapshotRequest(newer, Result.Success(ModuleSystemWorkbenchTestData.Snapshot(2)))
            .Should().BeTrue();

        session.Snapshot!.Revision.Should().Be(2);
    }

    [Fact]
    public async Task Baseline_import_rejects_other_schema_versions_and_compares_compatible_exports()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();

        await ImportAsync(session, ModuleSystemWorkbenchTestData.Export(schemaVersion: 99));
        session.BaselineError.Should().Be(ModuleDiagnosticsBaselineError.IncompatibleSchema);
        session.Comparison.Should().BeNull();

        await ImportAsync(session, ModuleSystemWorkbenchTestData.Export());
        session.BaselineError.Should().Be(ModuleDiagnosticsBaselineError.None);
        session.Comparison.Should().NotBeNull();
        session.Comparison!.TotalDurationDeltaMs.Should().Be(4);
        session.Comparison.ApplicationStartupDurationDeltaMs.Should().Be(6.125);
        session.Comparison.AddedModules.Should().ContainSingle().Which.Should().Contain("BetaModule");
        session.Comparison.AddedEdges.Should().ContainSingle().Which.Should().Be(
            $"{ModuleSystemWorkbenchTestData.BetaKey.Id} -> {ModuleSystemWorkbenchTestData.AlphaKey.Id}");

        await ImportAsync(session, ModuleSystemWorkbenchTestData.Export(applicationStartupDurationMs: null));
        session.Comparison.Should().NotBeNull();
        session.Comparison!.ApplicationStartupDurationDeltaMs.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_import_rejects_a_schema_compatible_but_structurally_invalid_export()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        var module = ModuleSystemWorkbenchTestData.Export().Modules[0];
        var invalid = ModuleSystemWorkbenchTestData.Export() with
        {
            Modules = [module, module]
        };

        await ImportAsync(session, invalid);

        session.BaselineError.Should().Be(ModuleDiagnosticsBaselineError.InvalidJson);
        session.Comparison.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_import_rejects_an_invalid_optional_application_startup_duration()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        var invalid = ModuleSystemWorkbenchTestData.Export(applicationStartupDurationMs: -1);

        await ImportAsync(session, invalid);

        session.BaselineError.Should().Be(ModuleDiagnosticsBaselineError.InvalidJson);
        session.Comparison.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_import_rejects_an_export_without_an_explicit_schema_version()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        var document = JsonNode.Parse(JsonSerializer.Serialize(ModuleSystemWorkbenchTestData.Export()))!.AsObject();
        document.Remove(nameof(ModuleDiagnosticsExport.SchemaVersion)).Should().BeTrue();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()));

        await session.ImportBaselineAsync(stream, TestContext.Current.CancellationToken);

        session.BaselineError.Should().Be(ModuleDiagnosticsBaselineError.InvalidJson);
        session.Comparison.Should().BeNull();
    }

    [Fact]
    public async Task Baseline_import_rejects_a_sparse_document_missing_required_sections()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        const string sparseJson = """
                                  { "schemaVersion": 2, "compositionId": "sparse-baseline" }
                                  """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sparseJson));

        await session.ImportBaselineAsync(stream, TestContext.Current.CancellationToken);

        session.BaselineError.Should().Be(ModuleDiagnosticsBaselineError.InvalidJson);
        session.Comparison.Should().BeNull();
    }

    [Fact]
    public async Task Filters_selection_drawer_and_lazy_diagnostics_share_one_session()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();

        session.SetModuleFilters(
            "Beta",
            ModuleCatalogStateFilter.Active,
            ModuleCatalogCapabilityFilter.Web,
            "Test.Web",
            onlyWithDependencies: true,
            minimumCostMs: 10);
        var selected = session.PagedModules.Should().ContainSingle().Subject;
        session.SelectModule(selected);
        session.IsModuleDrawerOpen.Should().BeTrue();

        await session.EnsureSelectedModuleOptionsLoadedAsync();
        await session.EnsureAssemblyInventoryLoadedAsync();

        session.SelectedModuleOptionsState.Should().Be(ModuleSystemLazyLoadState.Ready);
        session.SelectedModuleOptions!.Entries.Should().NotBeEmpty();
        session.AssemblyInventoryState.Should().Be(ModuleSystemLazyLoadState.Ready);
        session.FilteredAssemblies.First().Outcome.Should().Be(TypeDiscoveryAssemblyOutcome.ResolutionFailed);
    }

    [Fact]
    public async Task Dependency_exploration_defaults_to_one_hop_and_full_host_remains_explicit()
    {
        var firstKey = ModuleKey.FromModuleType(typeof(FirstGraphModule));
        var disabledBridgeKey = ModuleKey.FromModuleType(typeof(DisabledBridgeModule));
        var isolatedKey = ModuleKey.FromModuleType(typeof(IsolatedGraphModule));
        var first = ModuleSystemWorkbenchTestData.Module(firstKey, nameof(FirstGraphModule), "Test.Graph", 0, 10);
        var disabledBridge = ModuleSystemWorkbenchTestData.Module(
            disabledBridgeKey,
            nameof(DisabledBridgeModule),
            "Test.Graph",
            1,
            0) with { IsActive = false };
        var isolated = ModuleSystemWorkbenchTestData.Module(
            isolatedKey,
            nameof(IsolatedGraphModule),
            "Test.Graph",
            2,
            1);
        var snapshot = ModuleSystemWorkbenchTestData.Snapshot() with
        {
            Modules = [first, disabledBridge, isolated],
            BlockingChain = [],
            Topology = new ModuleDiagnosticsTopology
            {
                Edges =
                [
                    new ModuleDiagnosticsDependencyEdge
                    {
                        SourceModule = firstKey,
                        TargetModule = disabledBridgeKey
                    },
                    new ModuleDiagnosticsDependencyEdge
                    {
                        SourceModule = disabledBridgeKey,
                        TargetModule = isolatedKey
                    }
                ],
                TopologicalOrder = [firstKey, disabledBridgeKey, isolatedKey],
                MaximumDepth = 2
            }
        };
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls(
            snapshot: () => Result.Success(snapshot)));

        await session.InitializeAsync();

        session.ShowFullDependencyGraph.Should().BeFalse();
        session.VisibleDependencyModules.Should().ContainSingle().Which.ModuleKey.Should().Be(firstKey);
        session.VisibleDependencyEdges.Should().BeEmpty();

        session.SetShowFullDependencyGraph(true);
        session.VisibleDependencyModules.Select(static module => module.ModuleKey)
            .Should().BeEquivalentTo(new[] { firstKey, isolatedKey });
        session.VisibleDependencyEdges.Should().BeEmpty();

        session.ResetDependencyNeighborhood();
        session.ShowFullDependencyGraph.Should().BeFalse();
        session.DependencyNeighborhoodDepth.Should().Be(1);
    }

    [Fact]
    public async Task Selecting_another_module_clears_trace_evidence_owned_by_the_previous_module()
    {
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls());
        await session.InitializeAsync();
        var alphaSpan = session.Snapshot!.TraceSpans.Single();
        var beta = session.Snapshot.Modules.Single(module => module.ModuleKey == ModuleSystemWorkbenchTestData.BetaKey);

        session.SelectTraceSpan(alphaSpan);
        session.SelectModule(beta, openDrawer: false);

        session.SelectedModule.Should().BeSameAs(beta);
        session.SelectedTraceSpanId.Should().BeNull();
    }

    [Fact]
    public async Task Authorization_is_rechecked_before_every_facade_boundary()
    {
        var authorized = true;
        var snapshotCalls = 0;
        var inventoryCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(
                snapshot: () =>
                {
                    snapshotCalls++;
                    return Result.Success(ModuleSystemWorkbenchTestData.Snapshot());
                },
                inventory: () =>
                {
                    inventoryCalls++;
                    return Result.Success(ModuleSystemWorkbenchTestData.Inventory());
                }),
            authorize: () => Task.FromResult(authorized));

        await session.InitializeAsync();
        authorized = false;
        await session.RefreshAsync();
        await session.EnsureAssemblyInventoryLoadedAsync();

        snapshotCalls.Should().Be(1);
        inventoryCalls.Should().Be(0);
        session.IsAccessDenied.Should().BeTrue();
        session.AssemblyInventoryState.Should().Be(ModuleSystemLazyLoadState.Failed);
    }

    [Fact]
    public async Task Closing_the_drawer_clears_revealed_configuration_and_requires_fresh_authorization()
    {
        var authorizationChecks = 0;
        var optionCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(options: key =>
            {
                optionCalls++;
                return Result.Success(ModuleSystemWorkbenchTestData.Options(
                    key,
                    ModuleOptionDiagnosticsExposureMode.RevealSensitive));
            }),
            authorize: () =>
            {
                authorizationChecks++;
                return Task.FromResult(true);
            });
        await session.InitializeAsync();
        session.SelectModule(session.Snapshot!.Modules[0]);

        await session.EnsureSelectedModuleOptionsLoadedAsync();
        session.SelectedModuleOptions!.ContainsRevealedSensitiveValues.Should().BeTrue();
        session.CloseModuleDrawer();

        session.SelectedModuleOptions.Should().BeNull();
        session.SelectedModuleOptionsState.Should().Be(ModuleSystemLazyLoadState.NotRequested);

        session.SelectModule(session.Snapshot.Modules[0]);
        await session.EnsureSelectedModuleOptionsLoadedAsync();

        optionCalls.Should().Be(2);
        authorizationChecks.Should().Be(3, "initial snapshot plus both sensitive option disclosures are authorized");
    }

    [Fact]
    public async Task Leaving_the_configuration_tab_clears_revealed_configuration_and_requires_fresh_authorization()
    {
        var authorizationChecks = 0;
        var optionCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(options: key =>
            {
                optionCalls++;
                return Result.Success(ModuleSystemWorkbenchTestData.Options(
                    key,
                    ModuleOptionDiagnosticsExposureMode.RevealSensitive));
            }),
            authorize: () =>
            {
                authorizationChecks++;
                return Task.FromResult(true);
            });
        await session.InitializeAsync();
        session.SelectModule(session.Snapshot!.Modules[0]);
        session.SetModuleDrawerTab(3);

        await session.EnsureSelectedModuleOptionsLoadedAsync();
        session.SelectedModuleOptions!.ContainsRevealedSensitiveValues.Should().BeTrue();
        session.SetModuleDrawerTab(1);

        session.SelectedModuleOptions.Should().BeNull();
        session.SelectedModuleOptionsState.Should().Be(ModuleSystemLazyLoadState.NotRequested);

        session.SetModuleDrawerTab(3);
        await session.EnsureSelectedModuleOptionsLoadedAsync();

        optionCalls.Should().Be(2);
        authorizationChecks.Should().Be(3, "initial snapshot plus both sensitive option disclosures are authorized");
    }

    [Fact]
    public async Task Leaving_the_configuration_tab_preserves_the_redacted_projection_cache()
    {
        var authorizationChecks = 0;
        var optionCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(options: key =>
            {
                optionCalls++;
                return Result.Success(ModuleSystemWorkbenchTestData.Options(key));
            }),
            authorize: () =>
            {
                authorizationChecks++;
                return Task.FromResult(true);
            });
        await session.InitializeAsync();
        session.SelectModule(session.Snapshot!.Modules[0]);
        session.SetModuleDrawerTab(3);

        await session.EnsureSelectedModuleOptionsLoadedAsync();
        session.SetModuleDrawerTab(1);

        session.SelectedModuleOptions!.ExposureMode.Should().Be(ModuleOptionDiagnosticsExposureMode.Redacted);
        session.SelectedModuleOptionsState.Should().Be(ModuleSystemLazyLoadState.Ready);

        session.SetModuleDrawerTab(3);
        await session.EnsureSelectedModuleOptionsLoadedAsync();

        optionCalls.Should().Be(1);
        authorizationChecks.Should().Be(2, "the initial snapshot and first redacted projection require authorization");
    }

    [Theory]
    [InlineData(OptionRequestInvalidation.CloseDrawer)]
    [InlineData(OptionRequestInvalidation.SwitchModule)]
    [InlineData(OptionRequestInvalidation.Refresh)]
    [InlineData(OptionRequestInvalidation.OpenHelp)]
    [InlineData(OptionRequestInvalidation.LeaveConfigurationTab)]
    [InlineData(OptionRequestInvalidation.SelectSameModuleSummary)]
    [InlineData(OptionRequestInvalidation.SelectTraceSpan)]
    [InlineData(OptionRequestInvalidation.ReconcileSnapshot)]
    [InlineData(OptionRequestInvalidation.Dispose)]
    public async Task Pending_option_authorization_cannot_restore_configuration_after_lifetime_invalidation(
        OptionRequestInvalidation invalidation)
    {
        var authorizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizationChecks = 0;
        var optionCalls = 0;
        Task<bool> AuthorizeAsync()
        {
            var authorizationCheck = Interlocked.Increment(ref authorizationChecks);
            if (authorizationCheck == 1)
            {
                return Task.FromResult(true);
            }

            if (authorizationCheck == 2)
            {
                authorizationStarted.TrySetResult();
                return authorizationCompletion.Task;
            }

            return Task.FromResult(true);
        }

        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(options: key =>
            {
                Interlocked.Increment(ref optionCalls);
                return Result.Success(ModuleSystemWorkbenchTestData.Options(
                    key,
                    ModuleOptionDiagnosticsExposureMode.RevealSensitive));
            }),
            authorize: AuthorizeAsync);
        await session.InitializeAsync();
        var originalModule = session.Snapshot!.Modules[0];
        session.SelectModule(originalModule);
        session.SetModuleDrawerTab(3);

        var pendingLoad = session.EnsureSelectedModuleOptionsLoadedAsync();
        await authorizationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        switch (invalidation)
        {
            case OptionRequestInvalidation.CloseDrawer:
                session.CloseModuleDrawer();
                break;
            case OptionRequestInvalidation.SwitchModule:
                session.SelectModule(session.Snapshot.Modules[1]);
                break;
            case OptionRequestInvalidation.Refresh:
                await session.RefreshAsync();
                break;
            case OptionRequestInvalidation.OpenHelp:
                session.SetHelpDrawer(true);
                break;
            case OptionRequestInvalidation.LeaveConfigurationTab:
                session.SetModuleDrawerTab(1);
                break;
            case OptionRequestInvalidation.SelectSameModuleSummary:
                session.SelectModule(originalModule, drawerTabIndex: 0);
                break;
            case OptionRequestInvalidation.SelectTraceSpan:
                session.SelectTraceSpan(session.Snapshot.TraceSpans.Single());
                break;
            case OptionRequestInvalidation.ReconcileSnapshot:
                var requestVersion = session.BeginSnapshotRequest(isInitial: false);
                session.CompleteSnapshotRequest(
                        requestVersion,
                        Result.Success(ModuleSystemWorkbenchTestData.Snapshot(revision: 2)))
                    .Should().BeTrue();
                break;
            case OptionRequestInvalidation.Dispose:
                await session.DisposeAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, null);
        }

        var stateAfterInvalidation = session.SelectedModuleOptionsState;
        authorizationCompletion.SetResult(true);
        await pendingLoad;

        stateAfterInvalidation.Should().Be(ModuleSystemLazyLoadState.NotRequested);
        optionCalls.Should().Be(0);
        session.SelectedModuleOptions.Should().BeNull();
        if (invalidation != OptionRequestInvalidation.Dispose)
        {
            session.SelectModule(originalModule);
            session.SelectedModuleOptionsState.Should().Be(ModuleSystemLazyLoadState.NotRequested);
        }
    }

    [Fact]
    public async Task Polling_stops_as_soon_as_a_final_snapshot_is_observed()
    {
        var calls = 0;
        await using var session = new ModuleSystemWorkbenchSession(
            ModuleSystemWorkbenchTestData.Calls(snapshot: () => Result.Success(
                ModuleSystemWorkbenchTestData.Snapshot(++calls, isFinal: calls >= 2))),
            pollingInterval: TimeSpan.FromMilliseconds(5));

        await session.InitializeAsync();
        await WaitUntilAsync(() => calls >= 2);
        var terminalCalls = calls;
        await Task.Delay(30, TestContext.Current.CancellationToken);

        session.Snapshot!.IsFinal.Should().BeTrue();
        calls.Should().Be(terminalCalls);
    }

    private static async Task ImportAsync(
        ModuleSystemWorkbenchSession session,
        ModuleDiagnosticsExport export)
    {
        var json = JsonSerializer.Serialize(export);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await session.ImportBaselineAsync(stream, TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }

    private sealed class FirstGraphModule;
    private sealed class DisabledBridgeModule;
    private sealed class IsolatedGraphModule;

    public enum OptionRequestInvalidation
    {
        CloseDrawer,
        SwitchModule,
        Refresh,
        OpenHelp,
        LeaveConfigurationTab,
        SelectSameModuleSummary,
        SelectTraceSpan,
        ReconcileSnapshot,
        Dispose
    }
}
