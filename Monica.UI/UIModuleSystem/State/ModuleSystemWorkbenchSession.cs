using System.Text.Json;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.UI.UIModuleSystem.Support;

namespace Monica.UI.UIModuleSystem.State;

internal sealed record ModuleDiagnosticsCalls(
    Func<Res<ModuleDiagnosticsSnapshot>> GetSnapshot,
    Func<Res<TypeDiscoveryAssemblyInventory>> GetAssemblyInventory,
    Func<ModuleKey, Res<ModuleOptionDiagnostics>> GetModuleOptions,
    Func<Res<ModuleDiagnosticsExport>> CreateExport,
    Func<Res<IReadOnlyList<ResourceActivationObservation>>>? GetResourceActivations = null);

/// <summary>
/// Owns the immutable snapshot, filters, selection, lazy requests, comparison, and polling lifetime of one page.
/// </summary>
/// <remarks>
/// The facade itself is synchronous because diagnostics projection is host-local and revision-cached. This session does not
/// manufacture asynchronous facade wrappers; only the one-second polling clock and browser-supplied import stream are async.
/// </remarks>
public sealed class ModuleSystemWorkbenchSession : IAsyncDisposable
{
    private const int MODULE_DRAWER_CONFIGURATION_TAB_INDEX = 3;
    private static readonly JsonSerializerOptions IMPORT_JSON_OPTIONS = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ModuleDiagnosticsCalls _calls;
    private readonly Func<Task<bool>> _authorize;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TimeSpan _pollingInterval;
    private readonly Dictionary<ModuleKey, ModuleOptionDiagnostics> _optionsByModule = [];
    private readonly Dictionary<ModuleKey, ModuleOptionLoadEntry> _optionLoadsByModule = [];
    private PeriodicTimer? _pollingTimer;
    private Task? _pollingTask;
    private int _snapshotRequestVersion;
    private int _inventoryRequestVersion;
    private int _baselineRequestVersion;
    private int _optionRequestVersion;
    private int _activationRequestVersion;
    private bool _disposed;

    internal ModuleSystemWorkbenchSession(
        ModuleDiagnosticsCalls calls,
        Func<Task<bool>>? authorize = null,
        TimeSpan? pollingInterval = null)
    {
        _calls = calls;
        _authorize = authorize ?? (() => Task.FromResult(true));
        _pollingInterval = pollingInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Raised after an accepted state transition.</summary>
    public event Action? Changed;

    /// <summary>Gets the latest internally consistent snapshot.</summary>
    public ModuleDiagnosticsSnapshot? Snapshot { get; private set; }

    /// <summary>Gets the main snapshot request state.</summary>
    public ModuleSystemWorkbenchLoadState LoadState { get; private set; } =
        ModuleSystemWorkbenchLoadState.InitialLoading;

    /// <summary>Gets the initial-load failure, when no snapshot is available.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Gets the most recent refresh failure while older data remains visible.</summary>
    public string? RefreshError { get; private set; }

    /// <summary>Gets the last successful live resource admission view, separate from the startup snapshot.</summary>
    public IReadOnlyList<ResourceActivationObservation>? ResourceActivations { get; private set; }

    /// <summary>Gets the live resource view's read state; a failed refresh retains the previous successful view.</summary>
    public ModuleSystemLazyLoadState ResourceActivationsState { get; private set; }

    /// <summary>Gets whether the most recent facade boundary denied the current circuit.</summary>
    public bool IsAccessDenied { get; private set; }

    /// <summary>Gets the active deep-linkable section.</summary>
    public ModuleSystemWorkbenchSection Section { get; private set; }

    /// <summary>Gets the selected module shared by the ribbon, catalog, drawer, and graph.</summary>
    public ModuleDiagnosticsModule? SelectedModule { get; private set; }

    /// <summary>Gets whether the module detail drawer is open.</summary>
    public bool IsModuleDrawerOpen { get; private set; }

    /// <summary>Gets whether the help drawer is open.</summary>
    public bool IsHelpDrawerOpen { get; private set; }

    /// <summary>Gets the selected detail-drawer tab index.</summary>
    public int ModuleDrawerTabIndex { get; private set; }

    /// <summary>Gets the diagnostics trace interval selected by an evidence view.</summary>
    public string? SelectedTraceSpanId { get; private set; }

    /// <summary>Gets the lazy assembly inventory.</summary>
    public TypeDiscoveryAssemblyInventory? AssemblyInventory { get; private set; }

    /// <summary>Gets the lazy assembly inventory state.</summary>
    public ModuleSystemLazyLoadState AssemblyInventoryState { get; private set; }

    /// <summary>Gets the assembly-inventory load error.</summary>
    public string? AssemblyInventoryError { get; private set; }

    /// <summary>Gets the current portable comparison, when a compatible baseline has been imported.</summary>
    public ModuleDiagnosticsComparison? Comparison { get; private set; }

    /// <summary>Gets the stable import error code owned by UI localization.</summary>
    public ModuleDiagnosticsBaselineError BaselineError { get; private set; }

    /// <summary>Gets the current module search text.</summary>
    public string ModuleSearch { get; private set; } = string.Empty;

    /// <summary>Gets the current catalog state filter.</summary>
    public ModuleCatalogStateFilter ModuleStateFilter { get; private set; }

    /// <summary>Gets the current catalog capability filter.</summary>
    public ModuleCatalogCapabilityFilter ModuleCapabilityFilter { get; private set; }

    /// <summary>Gets the selected assembly name filter.</summary>
    public string? ModuleAssemblyFilter { get; private set; }

    /// <summary>Gets whether only modules with direct dependencies are shown.</summary>
    public bool OnlyModulesWithDependencies { get; private set; }

    /// <summary>Gets the minimum combined serial callback and startup-work cost.</summary>
    public double MinimumModuleCostMs { get; private set; }

    /// <summary>Gets the one-based module catalog page.</summary>
    public int ModulePage { get; private set; } = 1;

    /// <summary>Gets the module catalog page size.</summary>
    public int ModulePageSize { get; private set; } = 25;

    /// <summary>Gets the maximum contributor count; zero means all contributors.</summary>
    public int PerformanceContributorLimit { get; private set; } = 10;

    /// <summary>Gets the minimum contributor duration.</summary>
    public double PerformanceMinimumDurationMs { get; private set; } = 1;

    /// <summary>Gets whether zero-duration contributor rows are hidden.</summary>
    public bool HideZeroDurationContributors { get; private set; } = true;

    /// <summary>Gets whether the dependency view shows the complete compiled host topology.</summary>
    public bool ShowFullDependencyGraph { get; private set; }

    /// <summary>Gets the selected-module neighborhood radius.</summary>
    public int DependencyNeighborhoodDepth { get; private set; } = 1;

    /// <summary>Gets whether the dependency table alternative is expanded.</summary>
    public bool ShowDependencyTable { get; private set; }

    /// <summary>Gets the assembly inventory search.</summary>
    public string AssemblySearch { get; private set; } = string.Empty;

    /// <summary>Gets the active assembly explorer facet.</summary>
    public AssemblyInventoryFacet AssemblyFacet { get; private set; } = AssemblyInventoryFacet.ScanScope;

    /// <summary>Gets the one-based assembly inventory page.</summary>
    public int AssemblyPage { get; private set; } = 1;

    /// <summary>Gets the assembly inventory page size.</summary>
    public int AssemblyPageSize { get; private set; } = 10;

    /// <summary>Gets the modules after all catalog filters have been applied.</summary>
    public IReadOnlyList<ModuleDiagnosticsModule> FilteredModules
    {
        get
        {
            if (Snapshot is null)
            {
                return [];
            }

            IEnumerable<ModuleDiagnosticsModule> modules = Snapshot.Modules;
            if (!string.IsNullOrWhiteSpace(ModuleSearch))
            {
                var search = ModuleSearch.Trim();
                modules = modules.Where(module =>
                    module.TypeName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || module.FullTypeName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || module.AssemblyName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            modules = ModuleStateFilter switch
            {
                ModuleCatalogStateFilter.Active => modules.Where(static module => module.IsActive),
                ModuleCatalogStateFilter.Disabled => modules.Where(static module => !module.IsActive),
                _ => modules
            };
            modules = ModuleCapabilityFilter switch
            {
                ModuleCatalogCapabilityFilter.Ui => modules.Where(static module => module.IsUiModule),
                ModuleCatalogCapabilityFilter.Web => modules.Where(static module => module.IsWebModule),
                ModuleCatalogCapabilityFilter.WebHostRequired => modules.Where(static module => module.RequiresWebHost),
                _ => modules
            };
            if (!string.IsNullOrWhiteSpace(ModuleAssemblyFilter))
            {
                modules = modules.Where(module =>
                    string.Equals(module.AssemblyName, ModuleAssemblyFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (OnlyModulesWithDependencies)
            {
                modules = modules.Where(static module => module.DirectDependencyCount > 0);
            }

            if (MinimumModuleCostMs > 0)
            {
                modules = modules.Where(module => ModuleCost(module) >= MinimumModuleCostMs);
            }

            return modules
                .OrderByDescending(ModuleCost)
                .ThenBy(static module => module.RegistrationOrder ?? int.MaxValue)
                .ThenBy(static module => module.TypeName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>Gets the current page of the module catalog.</summary>
    public IReadOnlyList<ModuleDiagnosticsModule> PagedModules => FilteredModules
        .Skip((ModulePage - 1) * ModulePageSize)
        .Take(ModulePageSize)
        .ToArray();

    /// <summary>Gets the total module catalog page count.</summary>
    public int ModulePageCount => Math.Max(1, (int)Math.Ceiling(FilteredModules.Count / (double)ModulePageSize));

    /// <summary>Gets the available assembly filter values.</summary>
    public IReadOnlyList<string> ModuleAssemblies => Snapshot?.Modules
        .Select(static module => module.AssemblyName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    /// <summary>Gets the performance contributors under the workbench's explicit defaults.</summary>
    public IReadOnlyList<ModuleDiagnosticsModule> PerformanceContributors
    {
        get
        {
            if (Snapshot is null)
            {
                return [];
            }

            var contributors = Snapshot.Modules
                .Where(module => ModuleCost(module) >= PerformanceMinimumDurationMs)
                .Where(module => !HideZeroDurationContributors || ModuleCost(module) > 0)
                .OrderByDescending(ModuleCost)
                .ThenBy(static module => module.TypeName, StringComparer.Ordinal);
            return (PerformanceContributorLimit <= 0
                    ? contributors
                    : contributors.Take(PerformanceContributorLimit))
                .ToArray();
        }
    }

    /// <summary>Gets the one-hop neighborhood or explicit full-host dependency edge set.</summary>
    public IReadOnlyList<ModuleDiagnosticsDependencyEdge> VisibleDependencyEdges
    {
        get
        {
            if (Snapshot is null)
            {
                return [];
            }

            var activeKeys = Snapshot.Modules
                .Where(static module => module.IsActive)
                .Select(static module => module.ModuleKey)
                .ToHashSet();
            var activeEdges = Snapshot.Topology.Edges
                .Where(edge => activeKeys.Contains(edge.SourceModule)
                               && activeKeys.Contains(edge.TargetModule))
                .ToArray();
            if (ShowFullDependencyGraph || SelectedModule is null)
            {
                return activeEdges;
            }

            var visibleKeys = new HashSet<ModuleKey> { SelectedModule.ModuleKey };
            var frontier = new HashSet<ModuleKey>(visibleKeys);
            for (var depth = 0; depth < DependencyNeighborhoodDepth; depth++)
            {
                var nextFrontier = new HashSet<ModuleKey>();
                foreach (var edge in activeEdges)
                {
                    if (frontier.Contains(edge.SourceModule) && !visibleKeys.Contains(edge.TargetModule))
                    {
                        nextFrontier.Add(edge.TargetModule);
                    }

                    if (frontier.Contains(edge.TargetModule) && !visibleKeys.Contains(edge.SourceModule))
                    {
                        nextFrontier.Add(edge.SourceModule);
                    }
                }

                visibleKeys.UnionWith(nextFrontier);
                frontier = nextFrontier;
                if (frontier.Count == 0)
                {
                    break;
                }
            }

            return activeEdges
                .Where(edge => visibleKeys.Contains(edge.SourceModule) && visibleKeys.Contains(edge.TargetModule))
                .ToArray();
        }
    }

    /// <summary>Gets modules visible in the current dependency projection.</summary>
    public IReadOnlyList<ModuleDiagnosticsModule> VisibleDependencyModules
    {
        get
        {
            if (Snapshot is null)
            {
                return [];
            }

            if (ShowFullDependencyGraph || SelectedModule is null)
            {
                return Snapshot.Modules.Where(static module => module.IsActive).ToArray();
            }

            var keys = VisibleDependencyEdges
                .SelectMany(static edge => new[] { edge.SourceModule, edge.TargetModule })
                .Append(SelectedModule.ModuleKey)
                .ToHashSet();
            return Snapshot.Modules.Where(module => keys.Contains(module.ModuleKey)).ToArray();
        }
    }

    /// <summary>Gets the safely projected options already loaded for the selected module.</summary>
    public ModuleOptionDiagnostics? SelectedModuleOptions =>
        SelectedModule is not null && _optionsByModule.TryGetValue(SelectedModule.ModuleKey, out var options)
            ? options
            : null;

    /// <summary>Gets the selected module's bounded-configuration request state.</summary>
    public ModuleSystemLazyLoadState SelectedModuleOptionsState =>
        SelectedModule is not null && _optionLoadsByModule.TryGetValue(SelectedModule.ModuleKey, out var entry)
            ? entry.State
            : ModuleSystemLazyLoadState.NotRequested;

    /// <summary>Gets the selected module's bounded-configuration request error.</summary>
    public string? SelectedModuleOptionsError =>
        SelectedModule is not null && _optionLoadsByModule.TryGetValue(SelectedModule.ModuleKey, out var entry)
            ? entry.Error
            : null;

    /// <summary>Gets the number of assemblies that failed resolution or only partially loaded.</summary>
    public int AssemblyAttentionCount => AssemblyInventory is null
        ? 0
        : AssemblyInventory.ResolutionFailedCount + AssemblyInventory.PartialTypeLoadCount;

    /// <summary>Gets the number of assemblies in the resolved type-discovery scan scope.</summary>
    public int AssemblyScanScopeCount => AssemblyInventory is null
        ? 0
        : AssemblyInventory.ScannedCount
          + AssemblyInventory.NotScannedCount
          + AssemblyInventory.PartialTypeLoadCount;

    /// <summary>Gets assembly records after the selected facet, local search, and attention-first ordering.</summary>
    public IReadOnlyList<TypeDiscoveryAssemblyRecord> FilteredAssemblies
    {
        get
        {
            if (AssemblyInventory is null)
            {
                return [];
            }

            IEnumerable<TypeDiscoveryAssemblyRecord> assemblies = AssemblyInventory.Assemblies;
            assemblies = AssemblyFacet switch
            {
                AssemblyInventoryFacet.Attention => assemblies.Where(static assembly =>
                    assembly.Outcome is TypeDiscoveryAssemblyOutcome.ResolutionFailed
                        or TypeDiscoveryAssemblyOutcome.PartialTypeLoad),
                AssemblyInventoryFacet.ScanScope => assemblies.Where(static assembly =>
                    assembly.Outcome is TypeDiscoveryAssemblyOutcome.Scanned
                        or TypeDiscoveryAssemblyOutcome.ResolvedNotScanned
                        or TypeDiscoveryAssemblyOutcome.PartialTypeLoad),
                AssemblyInventoryFacet.Excluded => assemblies.Where(static assembly =>
                    assembly.Outcome == TypeDiscoveryAssemblyOutcome.Excluded),
                _ => assemblies
            };

            if (!string.IsNullOrWhiteSpace(AssemblySearch))
            {
                var search = AssemblySearch.Trim();
                assemblies = assemblies.Where(assembly =>
                    assembly.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (assembly.Version?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (assembly.Location?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (assembly.Failure?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            return assemblies
                .OrderBy(static assembly => AssemblyOutcomeOrder(assembly.Outcome))
                .ThenBy(static assembly => assembly.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>Gets the current assembly inventory page.</summary>
    public IReadOnlyList<TypeDiscoveryAssemblyRecord> PagedAssemblies => FilteredAssemblies
        .Skip((AssemblyPage - 1) * AssemblyPageSize)
        .Take(AssemblyPageSize)
        .ToArray();

    /// <summary>Gets the total assembly page count.</summary>
    public int AssemblyPageCount => Math.Max(1, (int)Math.Ceiling(FilteredAssemblies.Count / (double)AssemblyPageSize));

    /// <summary>Loads the first snapshot and starts the one-second live polling clock only when needed.</summary>
    public Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LoadState = ModuleSystemWorkbenchLoadState.InitialLoading;
        return RequestSnapshotAsync(isInitial: true);
    }

    /// <summary>Refreshes the snapshot while preserving the currently rendered revision.</summary>
    public Task RefreshAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InvalidateOptionDiagnosticsRequests();
        return RequestSnapshotAsync(isInitial: Snapshot is null);
    }

    /// <summary>Reads current native resource admission after rechecking the page's access policy.</summary>
    public async Task RefreshResourceActivationsAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var version = Interlocked.Increment(ref _activationRequestVersion);
        ResourceActivationsState = ModuleSystemLazyLoadState.Loading;
        NotifyChanged();
        if (!await EnsureAuthorizedAsync(() => version == Volatile.Read(ref _activationRequestVersion))) return;
        var result = _calls.GetResourceActivations?.Invoke();
        if (_disposed || version != Volatile.Read(ref _activationRequestVersion)) return;
        if (result is not null && result.IsOk(out var resources))
        {
            ResourceActivations = resources;
            ResourceActivationsState = ModuleSystemLazyLoadState.Ready;
        }
        else
        {
            ResourceActivationsState = ModuleSystemLazyLoadState.Failed;
        }
        NotifyChanged();
    }

    /// <summary>Updates the deep-linkable workbench section.</summary>
    public void SetSection(ModuleSystemWorkbenchSection section)
    {
        if (Section == section)
        {
            return;
        }

        Section = section;
        NotifyChanged();
    }

    /// <summary>Selects a module and synchronizes every workbench evidence view.</summary>
    public void SelectModule(ModuleDiagnosticsModule? module, bool openDrawer = true, int drawerTabIndex = 0)
    {
        var nextTabIndex = Math.Max(0, drawerTabIndex);
        if (SelectedModule?.ModuleKey != module?.ModuleKey
            || (ModuleDrawerTabIndex == MODULE_DRAWER_CONFIGURATION_TAB_INDEX
                && nextTabIndex != MODULE_DRAWER_CONFIGURATION_TAB_INDEX))
        {
            InvalidateOptionDiagnosticsRequests();
        }

        if (SelectedTraceSpanId is not null)
        {
            var selectedSpan = Snapshot?.TraceSpans.FirstOrDefault(span =>
                string.Equals(span.SpanId, SelectedTraceSpanId, StringComparison.Ordinal));
            if (module is null || selectedSpan?.ModuleKey != module.ModuleKey)
            {
                SelectedTraceSpanId = null;
            }
        }

        SelectedModule = module;
        IsModuleDrawerOpen = module is not null && openDrawer;
        if (IsModuleDrawerOpen)
        {
            IsHelpDrawerOpen = false;
        }
        ModuleDrawerTabIndex = nextTabIndex;
        NotifyChanged();
    }

    /// <summary>Selects a diagnostics trace interval and synchronizes its owning module when present.</summary>
    public void SelectTraceSpan(ModuleDiagnosticsTraceSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        SelectedTraceSpanId = span.SpanId;
        if (span.ModuleKey is { } moduleKey && Snapshot is not null)
        {
            if (SelectedModule?.ModuleKey != moduleKey
                || ModuleDrawerTabIndex == MODULE_DRAWER_CONFIGURATION_TAB_INDEX)
            {
                InvalidateOptionDiagnosticsRequests();
            }

            SelectedModule = Snapshot.Modules.FirstOrDefault(module => module.ModuleKey == moduleKey);
            IsModuleDrawerOpen = SelectedModule is not null;
            ModuleDrawerTabIndex = 1;
        }

        NotifyChanged();
    }

    /// <summary>Selects a module from its URL-safe, assembly-qualified portable identity.</summary>
    public void SelectModule(string? moduleId, bool openDrawer)
    {
        var module = Snapshot?.Modules.FirstOrDefault(candidate =>
            string.Equals(candidate.ModuleKey.Id, moduleId, StringComparison.Ordinal));
        SelectModule(module, openDrawer);
    }

    /// <summary>Closes the module detail drawer without clearing synchronized selection.</summary>
    public void CloseModuleDrawer()
    {
        IsModuleDrawerOpen = false;
        InvalidateOptionDiagnosticsRequests();
        NotifyChanged();
    }

    /// <summary>Opens or closes the help drawer.</summary>
    public void SetHelpDrawer(bool isOpen)
    {
        IsHelpDrawerOpen = isOpen;
        if (isOpen)
        {
            IsModuleDrawerOpen = false;
            InvalidateOptionDiagnosticsRequests();
        }
        NotifyChanged();
    }

    /// <summary>Sets the module drawer tab and lazily loads bounded configuration when that view opens.</summary>
    public void SetModuleDrawerTab(int tabIndex)
    {
        var nextTabIndex = Math.Max(0, tabIndex);
        if (ModuleDrawerTabIndex == MODULE_DRAWER_CONFIGURATION_TAB_INDEX
            && nextTabIndex != MODULE_DRAWER_CONFIGURATION_TAB_INDEX)
        {
            InvalidateOptionDiagnosticsRequests();
        }

        ModuleDrawerTabIndex = nextTabIndex;
        NotifyChanged();
    }

    /// <summary>Loads the separately cached assembly inventory on first disclosure.</summary>
    public async Task EnsureAssemblyInventoryLoadedAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (AssemblyInventoryState is ModuleSystemLazyLoadState.Loading or ModuleSystemLazyLoadState.Ready)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _inventoryRequestVersion);
        AssemblyInventoryState = ModuleSystemLazyLoadState.Loading;
        AssemblyInventoryError = null;
        NotifyChanged();
        if (!await EnsureAuthorizedAsync())
        {
            if (_disposed)
            {
                return;
            }

            AssemblyInventoryState = ModuleSystemLazyLoadState.Failed;
            AssemblyInventoryError = "Module diagnostics access was denied.";
            NotifyChanged();
            return;
        }

        CompleteAssemblyInventoryRequest(requestVersion, _calls.GetAssemblyInventory());
    }

    /// <summary>Returns the sanitized current-schema payload produced by Core after rechecking access.</summary>
    public async Task<Res<ModuleDiagnosticsExport>> CreateExportAsync()
    {
        return await EnsureAuthorizedAsync()
            ? _calls.CreateExport()
            : Res.Fail("Module diagnostics access was denied.");
    }

    /// <summary>Loads or retries the selected module's public configuration catalog with bounded values.</summary>
    public async Task EnsureSelectedModuleOptionsLoadedAsync(bool retry = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SelectedModule is null)
        {
            return;
        }

        var moduleKey = SelectedModule.ModuleKey;
        var existingState = _optionLoadsByModule.GetValueOrDefault(moduleKey)?.State
                            ?? ModuleSystemLazyLoadState.NotRequested;
        if (!retry && existingState is ModuleSystemLazyLoadState.Loading or ModuleSystemLazyLoadState.Ready)
        {
            return;
        }

        var requestVersion = Interlocked.Increment(ref _optionRequestVersion);
        _optionLoadsByModule[moduleKey] = new ModuleOptionLoadEntry(ModuleSystemLazyLoadState.Loading, null);
        NotifyChanged();
        var isAuthorized = await EnsureAuthorizedAsync(() => IsCurrentOptionRequest(requestVersion, moduleKey));
        if (!IsCurrentOptionRequest(requestVersion, moduleKey))
        {
            return;
        }

        if (!isAuthorized)
        {
            _optionLoadsByModule[moduleKey] = new ModuleOptionLoadEntry(
                ModuleSystemLazyLoadState.Failed,
                "Module diagnostics access was denied.");
            NotifyChanged();
            return;
        }

        var result = _calls.GetModuleOptions(moduleKey);
        if (!IsCurrentOptionRequest(requestVersion, moduleKey))
        {
            return;
        }

        if (result.IsFailed(out var error, out var options))
        {
            _optionLoadsByModule[moduleKey] = new ModuleOptionLoadEntry(
                ModuleSystemLazyLoadState.Failed,
                error.Message);
        }
        else
        {
            _optionsByModule[moduleKey] = options;
            _optionLoadsByModule[moduleKey] = new ModuleOptionLoadEntry(ModuleSystemLazyLoadState.Ready, null);
        }

        NotifyChanged();
    }

    /// <summary>Imports and validates one browser-supplied sanitized baseline.</summary>
    public async Task ImportBaselineAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stream);
        var requestVersion = Interlocked.Increment(ref _baselineRequestVersion);
        BaselineError = ModuleDiagnosticsBaselineError.None;
        NotifyChanged();

        ModuleDiagnosticsExport? baseline;
        using var importCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            baseline = await JsonSerializer.DeserializeAsync<ModuleDiagnosticsExport>(
                stream,
                IMPORT_JSON_OPTIONS,
                importCancellation.Token);
        }
        catch (OperationCanceledException) when (importCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (JsonException)
        {
            CompleteBaselineRequest(requestVersion, null, ModuleDiagnosticsBaselineError.InvalidJson);
            return;
        }

        if (baseline is null)
        {
            CompleteBaselineRequest(requestVersion, null, ModuleDiagnosticsBaselineError.InvalidJson);
            return;
        }

        if (baseline.SchemaVersion != ModuleDiagnosticsSnapshot.CURRENT_SCHEMA_VERSION)
        {
            CompleteBaselineRequest(requestVersion, null, ModuleDiagnosticsBaselineError.IncompatibleSchema);
            return;
        }

        if (!IsStructurallyValidBaseline(baseline))
        {
            CompleteBaselineRequest(requestVersion, null, ModuleDiagnosticsBaselineError.InvalidJson);
            return;
        }

        CompleteBaselineRequest(requestVersion, baseline, ModuleDiagnosticsBaselineError.None);
    }

    /// <summary>Removes the client-owned baseline without changing the live snapshot.</summary>
    public void RemoveBaseline()
    {
        Comparison = null;
        BaselineError = ModuleDiagnosticsBaselineError.None;
        NotifyChanged();
    }

    /// <summary>Updates all module catalog filters atomically and returns to page one.</summary>
    public void SetModuleFilters(
        string search,
        ModuleCatalogStateFilter state,
        ModuleCatalogCapabilityFilter capability,
        string? assembly,
        bool onlyWithDependencies,
        double minimumCostMs)
    {
        ModuleSearch = search ?? string.Empty;
        ModuleStateFilter = state;
        ModuleCapabilityFilter = capability;
        ModuleAssemblyFilter = string.IsNullOrWhiteSpace(assembly) ? null : assembly;
        OnlyModulesWithDependencies = onlyWithDependencies;
        MinimumModuleCostMs = Math.Max(0, minimumCostMs);
        ModulePage = 1;
        NotifyChanged();
    }

    /// <summary>Updates module catalog paging.</summary>
    public void SetModulePaging(int page, int pageSize)
    {
        ModulePageSize = pageSize is 25 or 50 or 100 ? pageSize : 25;
        ModulePage = Math.Clamp(page, 1, ModulePageCount);
        NotifyChanged();
    }

    /// <summary>Updates the explicit performance contributor visibility controls.</summary>
    public void SetPerformanceFilters(int limit, double minimumDurationMs, bool hideZeroDuration)
    {
        PerformanceContributorLimit = limit is 0 or 10 or 25 or 50 ? limit : 10;
        PerformanceMinimumDurationMs = Math.Max(0, minimumDurationMs);
        HideZeroDurationContributors = hideZeroDuration;
        NotifyChanged();
    }

    /// <summary>Switches between the complete host topology and the selected-module neighborhood.</summary>
    public void SetShowFullDependencyGraph(bool showFullGraph)
    {
        ShowFullDependencyGraph = showFullGraph;
        NotifyChanged();
    }

    /// <summary>Expands the selected neighborhood by one hop, up to three hops.</summary>
    public void ExpandDependencyNeighborhood()
    {
        DependencyNeighborhoodDepth = Math.Min(3, DependencyNeighborhoodDepth + 1);
        NotifyChanged();
    }

    /// <summary>Returns dependency exploration to its one-hop selected-module default.</summary>
    public void ResetDependencyNeighborhood()
    {
        DependencyNeighborhoodDepth = 1;
        ShowFullDependencyGraph = false;
        NotifyChanged();
    }

    /// <summary>Shows or hides the keyboard-accessible direct-edge table.</summary>
    public void SetShowDependencyTable(bool showTable)
    {
        ShowDependencyTable = showTable;
        NotifyChanged();
    }

    /// <summary>Updates assembly search and resets its page.</summary>
    public void SetAssemblySearch(string value)
    {
        AssemblySearch = value ?? string.Empty;
        AssemblyPage = 1;
        NotifyChanged();
    }

    /// <summary>Changes the assembly explorer facet and returns to its first page.</summary>
    public void SetAssemblyFacet(AssemblyInventoryFacet facet)
    {
        AssemblyFacet = facet;
        AssemblyPage = 1;
        NotifyChanged();
    }

    /// <summary>Updates assembly paging.</summary>
    public void SetAssemblyPaging(int page, int pageSize)
    {
        AssemblyPageSize = pageSize is 10 or 25 or 50 ? pageSize : 10;
        AssemblyPage = Math.Clamp(page, 1, AssemblyPageCount);
        NotifyChanged();
    }

    internal int BeginSnapshotRequest(bool isInitial)
    {
        var requestVersion = Interlocked.Increment(ref _snapshotRequestVersion);
        LoadState = isInitial
            ? ModuleSystemWorkbenchLoadState.InitialLoading
            : ModuleSystemWorkbenchLoadState.Refreshing;
        LoadError = null;
        RefreshError = null;
        NotifyChanged();
        return requestVersion;
    }

    internal bool CompleteSnapshotRequest(int requestVersion, Res<ModuleDiagnosticsSnapshot> result)
    {
        if (_disposed || requestVersion != Volatile.Read(ref _snapshotRequestVersion))
        {
            return false;
        }

        if (result.IsFailed(out var error, out var snapshot))
        {
            if (Snapshot is null)
            {
                LoadState = ModuleSystemWorkbenchLoadState.Failed;
                LoadError = error.Message;
            }
            else
            {
                LoadState = ModuleSystemWorkbenchLoadState.Ready;
                RefreshError = error.Message;
            }

            NotifyChanged();
            return true;
        }

        Snapshot = snapshot;
        LoadState = ModuleSystemWorkbenchLoadState.Ready;
        LoadError = null;
        RefreshError = null;
        ReconcileSelection(snapshot);
        if (Comparison is not null)
        {
            Comparison = ModuleDiagnosticsComparison.Create(snapshot, Comparison.Baseline);
        }

        if (snapshot.IsFinal)
        {
            StopPolling();
        }
        else
        {
            EnsurePolling();
        }

        NotifyChanged();
        return true;
    }

    internal bool CompleteAssemblyInventoryRequest(
        int requestVersion,
        Res<TypeDiscoveryAssemblyInventory> result)
    {
        if (_disposed || requestVersion != Volatile.Read(ref _inventoryRequestVersion))
        {
            return false;
        }

        if (result.IsFailed(out var error, out var inventory))
        {
            AssemblyInventoryState = ModuleSystemLazyLoadState.Failed;
            AssemblyInventoryError = error.Message;
        }
        else
        {
            AssemblyInventory = inventory;
            AssemblyInventoryState = ModuleSystemLazyLoadState.Ready;
            AssemblyInventoryError = null;
            AssemblyFacet = inventory.ResolutionFailedCount + inventory.PartialTypeLoadCount > 0
                ? AssemblyInventoryFacet.Attention
                : AssemblyInventoryFacet.ScanScope;
            AssemblyPage = 1;
        }

        NotifyChanged();
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        InvalidateOptionDiagnosticsRequests();
        _optionsByModule.Clear();
        _optionLoadsByModule.Clear();
        await _lifetimeCancellation.CancelAsync();
        StopPolling();
        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask;
            }
            catch (OperationCanceledException)
            {
                // Page-owned polling was canceled during deterministic teardown.
            }
        }

        Changed = null;
        _lifetimeCancellation.Dispose();
    }

    private async Task RequestSnapshotAsync(bool isInitial)
    {
        var requestVersion = BeginSnapshotRequest(isInitial);
        if (!await EnsureAuthorizedAsync())
        {
            return;
        }

        CompleteSnapshotRequest(requestVersion, _calls.GetSnapshot());
    }

    private void EnsurePolling()
    {
        if (_disposed || _pollingTask is { IsCompleted: false })
        {
            return;
        }

        _pollingTimer = new PeriodicTimer(_pollingInterval);
        _pollingTask = PollAsync(_pollingTimer, _lifetimeCancellation.Token);
    }

    private void StopPolling()
    {
        _pollingTimer?.Dispose();
        _pollingTimer = null;
    }

    private async Task PollAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_disposed || Snapshot?.IsFinal == true)
                {
                    return;
                }

                await RequestSnapshotAsync(isInitial: Snapshot is null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the page is disposed.
        }
    }

    private void ReconcileSelection(ModuleDiagnosticsSnapshot snapshot)
    {
        InvalidateOptionDiagnosticsRequests();
        if (SelectedTraceSpanId is not null
            && !snapshot.TraceSpans.Any(span =>
                string.Equals(span.SpanId, SelectedTraceSpanId, StringComparison.Ordinal)))
        {
            SelectedTraceSpanId = null;
        }

        if (SelectedModule is not null)
        {
            SelectedModule = snapshot.Modules.FirstOrDefault(module =>
                module.ModuleKey == SelectedModule.ModuleKey);
        }

        SelectedModule ??= ResolveLargestBlockingContributor(snapshot)
                           ?? snapshot.Modules
                               .Where(static module => module.IsActive)
                               .OrderByDescending(ModuleCost)
                               .FirstOrDefault();
    }

    private static ModuleDiagnosticsModule? ResolveLargestBlockingContributor(ModuleDiagnosticsSnapshot snapshot)
    {
        var key = snapshot.BlockingChain
            .OrderByDescending(static segment => segment.BlockingDurationMs)
            .Select(static segment => (ModuleKey?)segment.ModuleKey)
            .FirstOrDefault();
        return key is null
            ? null
            : snapshot.Modules.FirstOrDefault(module => module.ModuleKey == key.Value);
    }

    private void CompleteBaselineRequest(
        int requestVersion,
        ModuleDiagnosticsExport? baseline,
        ModuleDiagnosticsBaselineError error)
    {
        if (_disposed || requestVersion != Volatile.Read(ref _baselineRequestVersion))
        {
            return;
        }

        if (error != ModuleDiagnosticsBaselineError.None || baseline is null || Snapshot is null)
        {
            BaselineError = error == ModuleDiagnosticsBaselineError.None
                ? ModuleDiagnosticsBaselineError.SnapshotUnavailable
                : error;
            NotifyChanged();
            return;
        }

        Comparison = ModuleDiagnosticsComparison.Create(Snapshot, baseline);
        BaselineError = ModuleDiagnosticsBaselineError.None;
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        if (!_disposed)
        {
            Changed?.Invoke();
        }
    }

    private void InvalidateOptionDiagnosticsRequests()
    {
        Interlocked.Increment(ref _optionRequestVersion);
        var loadingModuleKeys = _optionLoadsByModule
            .Where(static pair => pair.Value.State == ModuleSystemLazyLoadState.Loading)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var moduleKey in loadingModuleKeys)
        {
            _optionLoadsByModule.Remove(moduleKey);
        }

        var revealedModuleKeys = _optionsByModule
            .Where(static pair =>
                pair.Value.ExposureMode == ModuleOptionDiagnosticsExposureMode.RevealSensitive)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var moduleKey in revealedModuleKeys)
        {
            _optionsByModule.Remove(moduleKey);
            _optionLoadsByModule.Remove(moduleKey);
        }
    }

    private bool IsCurrentOptionRequest(int requestVersion, ModuleKey moduleKey) =>
        !_disposed
        && requestVersion == Volatile.Read(ref _optionRequestVersion)
        && SelectedModule?.ModuleKey == moduleKey;

    private async Task<bool> EnsureAuthorizedAsync(Func<bool>? canAcceptCompletion = null)
    {
        if (_disposed || canAcceptCompletion?.Invoke() == false)
        {
            return false;
        }

        var isAuthorized = await _authorize();
        if (_disposed || canAcceptCompletion?.Invoke() == false)
        {
            return false;
        }

        IsAccessDenied = !isAuthorized;
        if (!isAuthorized)
        {
            StopPolling();
            if (Snapshot is null)
            {
                LoadState = ModuleSystemWorkbenchLoadState.Failed;
            }

            NotifyChanged();
        }

        return isAuthorized;
    }

    private static double ModuleCost(ModuleDiagnosticsModule module) =>
        module.SerialCallbackDurationMs + module.StartupWorkDurationMs;

    private static bool IsStructurallyValidBaseline(ModuleDiagnosticsExport baseline)
    {
        if (string.IsNullOrWhiteSpace(baseline.CompositionId)
            || baseline.Summary is null
            || baseline.Modules.IsDefault
            || baseline.Edges.IsDefault
            || baseline.TraceSpans.IsDefault
            || !IsFiniteNonNegative(baseline.Summary.TotalCompositionDurationMs)
            || (baseline.Summary.ApplicationStartupDurationMs is { } applicationStartupDurationMs
                && !IsFiniteNonNegative(applicationStartupDurationMs))
            || !IsFiniteNonNegative(baseline.Summary.TypeDiscoveryDurationMs))
        {
            return false;
        }

        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in baseline.Modules)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleId)
                || string.IsNullOrWhiteSpace(module.TypeName)
                || string.IsNullOrWhiteSpace(module.AssemblyName)
                || !moduleIds.Add(module.ModuleId))
            {
                return false;
            }
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in baseline.Edges)
        {
            if (!moduleIds.Contains(edge.SourceModuleId)
                || !moduleIds.Contains(edge.TargetModuleId)
                || !edgeIds.Add($"{edge.SourceModuleId}\u001f{edge.TargetModuleId}"))
            {
                return false;
            }
        }

        var spanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in baseline.TraceSpans)
        {
            if (string.IsNullOrWhiteSpace(span.SpanId)
                || !spanIds.Add(span.SpanId)
                || !IsFiniteNonNegative(span.StartedOffsetMs)
                || !double.IsFinite(span.EndedOffsetMs)
                || span.EndedOffsetMs < span.StartedOffsetMs
                || (span.ModuleId is not null && !moduleIds.Contains(span.ModuleId)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static int AssemblyOutcomeOrder(TypeDiscoveryAssemblyOutcome outcome) => outcome switch
    {
        TypeDiscoveryAssemblyOutcome.ResolutionFailed => 0,
        TypeDiscoveryAssemblyOutcome.PartialTypeLoad => 1,
        TypeDiscoveryAssemblyOutcome.Scanned => 2,
        TypeDiscoveryAssemblyOutcome.ResolvedNotScanned => 3,
        TypeDiscoveryAssemblyOutcome.Excluded => 4,
        _ => 5
    };

    private sealed record ModuleOptionLoadEntry(ModuleSystemLazyLoadState State, string? Error);
}

/// <summary>Defines the factual lenses available in the assembly inventory explorer.</summary>
public enum AssemblyInventoryFacet
{
    /// <summary>Shows resolution failures and partial type loads.</summary>
    Attention,

    /// <summary>Shows assemblies that belong to the resolved type-discovery scan scope.</summary>
    ScanScope,

    /// <summary>Shows assemblies excluded from the scan scope.</summary>
    Excluded,

    /// <summary>Shows every assembly considered by the inventory.</summary>
    All
}

/// <summary>Defines stable baseline import failures whose display text belongs to Monica.UI.</summary>
public enum ModuleDiagnosticsBaselineError
{
    /// <summary>No error is present.</summary>
    None,

    /// <summary>The selected file is not a valid diagnostics export.</summary>
    InvalidJson,

    /// <summary>The selected export uses a different schema version.</summary>
    IncompatibleSchema,

    /// <summary>No current snapshot is available for comparison.</summary>
    SnapshotUnavailable
}
