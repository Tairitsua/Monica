using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Components;
using Monica.Configuration.UI.Dialogs;
using Monica.Configuration.UI.Localization;
using Monica.Configuration.UI.Models;
using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;
using Monica.Configuration.Utils;
using Monica.Core.Results;
using Monica.Tool.Extensions;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Configuration.UI.Pages;

/// <summary>Owns the configuration State route, its borrowed drafts, reads, and native dialog operations.</summary>
public partial class ConfigurationStatePage
{
    /// <summary>Gets the Configuration State workspace route.</summary>
    public const string PAGE_URL = ConfigurationUiRoutes.STATE_ROUTE;
    private const int SOURCE_VALIDATION_PANEL = 0;
    private const int OPTIONS_VALIDATION_PANEL = 1;

    private readonly List<ConfigurationDefinitionSummary> _definitions = [];
    private readonly Dictionary<string, ConfigurationDefinition> _definitionDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<LogicalPath, ConfigurationEffectiveValue> _effectiveValues = [];
    private readonly Dictionary<LogicalPath, ConfigurationSourceDescriptor> _externalEffectiveSources = [];
    private ConfigurationDefinitionSummary? _selectedSummary;
    private ConfigurationDefinition? _selectedDefinition;
    private ConfigurationStartupCandidate? _startupCandidate;
    private bool _isUnpersisted;
    private ConfigurationNodeDefinition? _selectedNode;
    private string _searchText = string.Empty;
    private ConfigurationDefinitionLifecycleFilter _lifecycleFilter = ConfigurationDefinitionLifecycleFilter.Active;
    private bool _loadingDefinitions = true;
    private bool _loadingDefinition;
    private int _pendingCount;
    private int _validationIssueCount;
    private string? _requestedDefinitionKey;
    private LogicalPath? _requestedPath;
    private LogicalPath? _highlightedPath;
    private ConfigurationValidationReport? _runtimeValidationReport;
    private IReadOnlyList<ConfigurationOptionsValidationReport> _optionsValidationReports = [];
    private string? _runtimeValidationError;
    private string? _optionsValidationError;
    private bool _validationLoading;
    private long _validationGeneration;
    private readonly CancellationTokenSource _validationLifetime = new();
    private CancellationTokenSource? _validationCancellation;
    private bool _runtimeValidationPopoverOpen;
    private int _validationPanelIndex;
    private ConfigurationReloadStatusButton? _reloadStatusButton;
    private ConfigurationMetadataStoreDiagnostic? _metadataStoreDiagnostic;
    private bool _definitionCatalogHealthy;
    private bool _disposed;
    private Task _pendingStateRender = Task.CompletedTask;
    private readonly object _retirementGate = new();
    private ConfigurationOperationLifetime _routeLifetime = new();
    private readonly List<ConfigurationOperationLifetime> _routeLifetimes = [];
    private (string? DefinitionKey, string? Path)? _routeIdentity;
    private TaskCompletionSource? _retirement;
    private long _catalogGeneration;
    private long _selectionGeneration;
    private long _valueReadGeneration;

    /// <summary>Gets the exact definition identity supplied by State-route navigation.</summary>
    [SupplyParameterFromQuery(Name = "definitionKey")]
    public string? RouteDefinitionKey { get; set; }

    /// <summary>Gets the logical path query text, parsed through the existing configuration route contract.</summary>
    [SupplyParameterFromQuery(Name = "path")]
    public string? RoutePath { get; set; }

    private bool CanInspectSelectedDefinitionSources =>
        _selectedDefinition?.Origin == ConfigurationDefinitionOrigin.LocalScan;

    private bool CanMutateSelectedDefinition => _selectedSummary?.CanMutate is true;

    private bool IsSelectedDefinitionRetired =>
        _selectedSummary?.LifecycleState == ConfigurationDefinitionLifecycleState.Retired;

    private bool CanUseCatalogOperations => !_loadingDefinitions && _definitionCatalogHealthy;

    private bool CanSavePendingGroup
    {
        get
        {
            var affectedDefinitionKeys = StateStore.GetPending()
                .Select(static change => change.DefinitionKey)
                .Concat(StateStore.GetValidationIssues().Select(static issue => issue.DefinitionKey))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            return affectedDefinitionKeys.All(definitionKey => _definitions.Any(definition =>
                definition.CanMutate
                && string.Equals(definition.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private string CatalogOperationTooltip(string actionKey)
    {
        return CanUseCatalogOperations
            ? L[actionKey].Value
            : L["State:Definitions:CatalogOperationBlocked"].Value;
    }

    private string ReloadBehaviorLabel(ConfigurationReloadBehavior behavior)
    {
        return ConfigurationReloadBehaviorFormatter.Label(behavior, L);
    }

    private static Color ReloadBehaviorColor(ConfigurationReloadBehavior behavior)
    {
        return behavior.RequiresProcessRestart() ? Color.Warning : Color.Info;
    }

    private IReadOnlyList<string> SearchTerms => string.IsNullOrWhiteSpace(_searchText)
        ? []
        : _searchText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private IReadOnlyDictionary<string, IReadOnlyList<ConfigurationNodeSearchMatch>> NodeSearchMatches =>
        string.IsNullOrWhiteSpace(_searchText)
            ? new Dictionary<string, IReadOnlyList<ConfigurationNodeSearchMatch>>(StringComparer.OrdinalIgnoreCase)
            : BuildNodeSearchMatches();

    private IReadOnlyDictionary<string, int> PendingCountsByDefinition =>
        StateStore.GetPending()
            .GroupBy(change => change.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, int> RuntimeValidationCountsByDefinition =>
        _runtimeValidationReport?.Issues
            .GroupBy(issue => issue.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private int RuntimeValidationIssueCount => _runtimeValidationReport?.IssueCount ?? 0;

    private Color RuntimeValidationStatusColor =>
        _validationLoading ? Color.Info : _runtimeValidationReport is null
            ? Color.Warning
            : ConfigurationValidationPresentation.StatusColor(_runtimeValidationReport.Coverage, _runtimeValidationReport.IsValid);

    private string RuntimeValidationStatusIcon =>
        _validationLoading ? MoIcons.Hourglass : _runtimeValidationReport is null
            ? MoIcons.Help
            : _runtimeValidationReport.IsValid
                ? MoIcons.Success
                : MoIcons.Warning;

    private string RuntimeValidationStatusTooltip =>
        $"{L["Validation:SourceTitle"]}: {RuntimeValidationSummary}";

    private string RuntimeValidationSummary =>
        _validationLoading ? L["Validation:Pending"].Value : _runtimeValidationReport is null
            ? L["State:RuntimeValidation:Unavailable"].Value
            : _runtimeValidationReport.Coverage switch
            {
                ConfigurationValidationCoverage.Failed => L["Validation:FailedSummary"].Value,
                ConfigurationValidationCoverage.SchemaOnly => L["Validation:SchemaOnlySummary"].Value,
                _ => _runtimeValidationReport.IsValid
                    ? L["State:RuntimeValidation:ValidSummary"].Value
                    : L["State:RuntimeValidation:InvalidSummary", RuntimeValidationIssueCount, RuntimeValidationCountsByDefinition.Count].Value
            };

    private IReadOnlyList<ConfigurationValidationIssue> RuntimeValidationIssues =>
        _runtimeValidationReport?.Issues.Select(ToUiValidationIssue).ToArray() ?? [];

    private int OptionsValidationIssueCount => _optionsValidationReports.Sum(report => report.Issues.Count);

    private Color OptionsValidationStatusColor => _validationLoading ? Color.Info
        : _optionsValidationError is not null ? Color.Error
        : _optionsValidationReports.Any(report => report.Coverage == ConfigurationValidationCoverage.Failed) ? Color.Error
        : _optionsValidationReports.Any(report => report.Coverage == ConfigurationValidationCoverage.SchemaOnly) ? Color.Warning
        : _optionsValidationReports.Any(report => !report.IsValid) ? Color.Error
        : _optionsValidationReports.Count == 0 || UnobservedDefinitionKeys.Any() ? Color.Default
        : _optionsValidationReports.All(report => report.IsValid) ? Color.Success : Color.Error;

    private string OptionsValidationStatusIcon => _validationLoading ? MoIcons.Hourglass
        : _optionsValidationError is not null ? MoIcons.Warning
        : _optionsValidationReports.Any(report => !report.IsValid) ? MoIcons.Warning
        : _optionsValidationReports.Count == 0 || UnobservedDefinitionKeys.Any() ? MoIcons.EyeOff
        : _optionsValidationReports.All(report => report.IsValid) ? MoIcons.Success : MoIcons.Warning;

    private string OptionsValidationSummary =>
        _validationLoading ? L["Validation:Pending"].Value
        : _optionsValidationError is not null ? L["Validation:Unavailable"].Value
        : _optionsValidationReports.Any(report => report.Coverage == ConfigurationValidationCoverage.Failed) ? L["Validation:FailedSummary"].Value
        : _optionsValidationReports.Any(report => report.Coverage == ConfigurationValidationCoverage.SchemaOnly) ? L["Validation:SchemaOnlySummary"].Value
        : _optionsValidationReports.Any(report => !report.IsValid) ? L["Validation:Invalid"].Value
        : _optionsValidationReports.Count == 0 || UnobservedDefinitionKeys.Any() ? L["Validation:NoObserved"].Value
        : _optionsValidationReports.All(report => report.IsValid) ? L["Validation:Valid"].Value : L["Validation:Invalid"].Value;

    private string OptionsValidationTooltip => $"{L["Validation:OptionsTitle"]}: {OptionsValidationSummary}";

    private bool IsOptionsValidationPanel => _validationPanelIndex == OPTIONS_VALIDATION_PANEL;

    private string ValidationPopoverTitle =>
        L[IsOptionsValidationPanel ? "Validation:OptionsTitle" : "Validation:SourceTitle"].Value;

    private string ValidationPopoverStatusIcon =>
        IsOptionsValidationPanel ? OptionsValidationStatusIcon : RuntimeValidationStatusIcon;

    private Color ValidationPopoverStatusColor =>
        IsOptionsValidationPanel ? OptionsValidationStatusColor : RuntimeValidationStatusColor;

    private string ValidationPopoverStatusIconStyle => ValidationPopoverStatusColor switch
    {
        Color.Info => "color:var(--mud-palette-info)",
        Color.Success => "color:var(--mud-palette-success)",
        Color.Warning => "color:var(--mud-palette-warning)",
        Color.Error => "color:var(--mud-palette-error)",
        _ => "color:var(--mud-palette-text-secondary)"
    };

    private int ValidationPopoverIssueCount =>
        IsOptionsValidationPanel ? OptionsValidationIssueCount : RuntimeValidationIssueCount;

    private string ValidationPopoverSummary =>
        IsOptionsValidationPanel ? OptionsValidationSummary : RuntimeValidationSummary;

    private IEnumerable<string> UnobservedDefinitionKeys =>
        _definitions.Select(definition => definition.DefinitionKey)
            .Union((_runtimeValidationReport?.DefinitionReports ?? []).Select(report => report.DefinitionKey),
                StringComparer.OrdinalIgnoreCase)
            .Except(_optionsValidationReports.Select(report => report.DefinitionKey), StringComparer.OrdinalIgnoreCase);

    private string ValidationDefinitionName(string definitionKey) =>
        _definitions.FirstOrDefault(definition => string.Equals(definition.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? definitionKey;

    private IReadOnlyList<ConfigurationSectionItem> SectionItems =>
        _selectedDefinition is null
            ? []
            : EnumerateSectionNodes(_selectedDefinition.Root)
                .Select(BuildSectionItem)
                .ToArray();

    private IEnumerable<ConfigurationNodeDefinition> VisibleNodes
    {
        get
        {
            if (_selectedNode is null)
            {
                return [];
            }

            if (IsSimpleEditableNode(_selectedNode))
            {
                return [_selectedNode];
            }

            if (_selectedNode.RelativePath.Depth == 0)
            {
                var rootItems = EnumerateNodes(_selectedNode)
                    .Where(node => node.RelativePath.Depth > 0
                                   && (IsSimpleEditableNode(node) || IsComplexNodeItem(node)))
                    .ToArray();
                return rootItems.Length == 0 ? [_selectedNode] : rootItems;
            }

            var leaves = EnumerateNodes(_selectedNode).Where(IsSimpleEditableNode).ToArray();
            return leaves.Length == 0 ? [_selectedNode] : leaves;
        }
    }

    protected override Task OnInitializedAsync()
    {
        StateStore.Changed += OnStateStoreChanged;
        return RefreshPendingCount();
    }

    protected override async Task OnParametersSetAsync()
    {
        ConfigurationOperationLifetime? previous;
        ConfigurationOperationLifetime route;
        lock (_retirementGate)
        {
            if (_disposed || _routeIdentity == (RouteDefinitionKey, RoutePath)) return;
            previous = _routeIdentity is null ? null : _routeLifetime;
            _routeIdentity = (RouteDefinitionKey, RoutePath);
            route = previous is null ? _routeLifetime : new ConfigurationOperationLifetime();
            _routeLifetime = route;
            _routeLifetimes.Add(route);
        }

        ClearSelectedDefinition();
        _loadingDefinitions = true;
        _definitionCatalogHealthy = false;
        CloseRuntimeValidationPopover();
        if (previous is not null) await previous.RetireAsync();
        if (route.IsRetired || _disposed || !ReferenceEquals(route, _routeLifetime)) return;
        ReadRouteTarget();
        await RunOwnedAsync((owner, token) => LoadDefinitionsAsyncCore(owner, token));
    }

    /// <summary>Rejects new route work, closes owned dialogs, and joins actual reads and admitted persistence.</summary>
    /// <remarks>The circuit-scoped draft store is borrowed and remains alive after this route retires.</remarks>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        ConfigurationOperationLifetime[] owners;
        lock (_retirementGate)
        {
            if (_retirement is not null) return new(_retirement.Task);
            _disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion;
            owners = _routeLifetimes.Append(_routeLifetime).Distinct().ToArray();
        }

        StateStore.Changed -= OnStateStoreChanged;
        _ = CompleteRetirementAsync(owners, completion);
        return new(completion.Task);
    }

    private async Task CompleteRetirementAsync(ConfigurationOperationLifetime[] owners, TaskCompletionSource completion)
    {
        var retirements = owners.Select(owner => owner.RetireAsync().AsTask()).ToArray();
        var failures = new List<Exception>();
        foreach (var retirement in retirements)
        {
            try { await retirement; }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { await _pendingStateRender; }
        catch (Exception exception) { failures.Add(exception); }
        try { _validationLifetime.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Configuration State retirement failed.", failures));
    }

    private async Task RunOwnedAsync(Func<ConfigurationOperationLifetime, CancellationToken, Task> operation)
    {
        var route = _routeLifetime;
        if (_disposed || route.IsRetired) return;
        try { await route.RunAsync(token => operation(route, token)); }
        catch (OperationCanceledException) when (route.IsRetired) { }
    }

    private Task<T> RunOwnedAsync<T>(Func<ConfigurationOperationLifetime, CancellationToken, Task<T>> operation)
    {
        var route = _routeLifetime;
        return route.RunAsync(token => operation(route, token));
    }

    private Task LoadDefinitionsAsync() =>
        RunOwnedAsync((route, token) => LoadDefinitionsAsyncCore(route, token));

    private async Task LoadDefinitionsAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var generation = ++_catalogGeneration;
        _loadingDefinitions = true;
        _definitionCatalogHealthy = false;
        var previouslySelectedKey = _selectedSummary?.DefinitionKey;
        var result = await Facade.GetDefinitionCatalogAsync();
        if (route.IsRetired || generation != _catalogGeneration)
        {
            return;
        }

        if (result.IsFailed(out var error, out var catalog))
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            _definitions.Clear();
            _metadataStoreDiagnostic = null;
            _runtimeValidationReport = null;
            ++_validationGeneration;
            _validationCancellation?.Cancel();
            _validationLoading = false;
            _optionsValidationReports = [];
            _optionsValidationError = L["Validation:Unavailable"].Value;
            ClearSelectedDefinition();
            _loadingDefinitions = false;
            return;
        }

        _definitions.Clear();
        _definitions.AddRange(catalog.Definitions);
        _metadataStoreDiagnostic = catalog.StoreDiagnostic;
        _definitionCatalogHealthy = catalog.IsHealthy;
        _definitionDetails.Clear();
        ClearSelectedDefinition();
        await LoadRuntimeValidationReportAsyncCore(route, token);
        if (route.IsRetired || generation != _catalogGeneration) return;
        _loadingDefinitions = false;

        var requestedSummary = ResolveRequestedDefinitionSummary();
        if (!string.IsNullOrWhiteSpace(_requestedDefinitionKey) && requestedSummary is null)
        {
            var message = _metadataStoreDiagnostic is null
                ? L["State:Definitions:RequestedDefinitionNotFound", _requestedDefinitionKey]
                : L["State:Definitions:RequestedDefinitionUnresolved", _requestedDefinitionKey];
            Snackbar.Add(message, Severity.Warning);
            ClearRequestedRouteTarget();
            return;
        }

        if (requestedSummary is { CanManage: false })
        {
            ClearRequestedRouteTarget();
            await ShowDefinitionMetadataProblemAsyncCore(requestedSummary, route, token);
            return;
        }

        if (requestedSummary is not null
            && !_lifecycleFilter.Includes(requestedSummary.LifecycleState))
        {
            _lifecycleFilter = requestedSummary.LifecycleState == ConfigurationDefinitionLifecycleState.Retired
                ? ConfigurationDefinitionLifecycleFilter.Retired
                : ConfigurationDefinitionLifecycleFilter.Active;
        }

        var initialSummary = requestedSummary
            ?? _definitions.FirstOrDefault(definition =>
                definition.CanManage
                && _lifecycleFilter.Includes(definition.LifecycleState)
                && string.Equals(definition.DefinitionKey, previouslySelectedKey, StringComparison.OrdinalIgnoreCase))
              ?? _definitions.FirstOrDefault(definition =>
                  definition.CanMutate && _lifecycleFilter.Includes(definition.LifecycleState))
              ?? _definitions.FirstOrDefault(definition =>
                  definition.CanManage && _lifecycleFilter.Includes(definition.LifecycleState));
        if (initialSummary is not null)
        {
            await SelectDefinitionAsyncCore(initialSummary, route, token);
            if (route.IsRetired || generation != _catalogGeneration
                || !string.Equals(_selectedDefinition?.DefinitionKey, initialSummary.DefinitionKey, StringComparison.Ordinal)) return;
            if (requestedSummary is not null)
            {
                ApplyRequestedPath();
            }
            else
            {
                ClearRequestedRouteTarget();
            }
        }
        else
        {
            ClearRequestedRouteTarget();
        }
    }

    private void ClearSelectedDefinition()
    {
        ++_selectionGeneration;
        ++_valueReadGeneration;
        _loadingDefinition = false;
        _selectedSummary = null;
        _selectedDefinition = null;
        _startupCandidate = null;
        _isUnpersisted = false;
        _selectedNode = null;
        _highlightedPath = null;
        _effectiveValues.Clear();
        _externalEffectiveSources.Clear();
    }

    private Task LoadRuntimeValidationReportAsync() =>
        RunOwnedAsync((route, token) => LoadRuntimeValidationReportAsyncCore(route, token));

    private async Task LoadRuntimeValidationReportAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        if (route.IsRetired)
        {
            return;
        }

        var generation = ++_validationGeneration;
        _validationCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _validationLifetime.Token);
        var validationToken = cancellation.Token;
        _validationCancellation = cancellation;
        _validationLoading = true;
        _runtimeValidationError = null;
        _optionsValidationError = null;
        try
        {
            var sourceTask = Facade.GetRuntimeValidationReportAsync(validationToken);
            var optionsTask = Facade.GetOptionsValidationReportsAsync(validationToken);
            await Task.WhenAll(sourceTask, optionsTask);
            if (route.IsRetired || generation != _validationGeneration)
            {
                return;
            }

            var sourceResult = await sourceTask;
            if (sourceResult.IsFailed(out var sourceError, out var report))
            {
                _runtimeValidationError = sourceError.Message ?? L["Validation:Unavailable"].Value;
                _runtimeValidationReport = null;
            }
            else
            {
                _runtimeValidationReport = report;
            }

            var optionsResult = await optionsTask;
            if (optionsResult.IsFailed(out var optionsError, out var attempts))
            {
                _optionsValidationError = optionsError.Message ?? L["Validation:Unavailable"].Value;
                _optionsValidationReports = [];
            }
            else
            {
                _optionsValidationReports = attempts;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_validationCancellation, cancellation)) _validationCancellation = null;
            if (!route.IsRetired && generation == _validationGeneration)
            {
                _validationLoading = false;
            }
        }
    }

    private void ToggleRuntimeValidationPopover(int panelIndex)
    {
        var closeCurrentPanel = _runtimeValidationPopoverOpen && _validationPanelIndex == panelIndex;
        _validationPanelIndex = panelIndex;
        _runtimeValidationPopoverOpen = !closeCurrentPanel;
    }

    private void OpenRuntimeValidationPopover()
    {
        _validationPanelIndex = SOURCE_VALIDATION_PANEL;
        _runtimeValidationPopoverOpen = true;
    }

    private void CloseRuntimeValidationPopover()
    {
        _runtimeValidationPopoverOpen = false;
    }

    private static ConfigurationValidationIssue ToUiValidationIssue(ConfigurationRuntimeValidationIssue issue)
        => ConfigurationValidationPresentation.FromRuntime(issue);

    private void ReadRouteTarget()
    {
        _requestedDefinitionKey = ConfigurationUiRoutes.ReadDefinitionKey(NavigationManager.Uri);
        try
        {
            var requestedPath = ConfigurationUiRoutes.ReadPath(NavigationManager.Uri);
            _requestedPath = requestedPath.Depth == 0 ? null : requestedPath;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"{L["Common:Errors:InvalidPath"]}: {ex.Message}", Severity.Error);
        }
    }

    private ConfigurationDefinitionSummary? ResolveRequestedDefinitionSummary()
    {
        return string.IsNullOrWhiteSpace(_requestedDefinitionKey)
            ? null
            : _definitions.FirstOrDefault(definition =>
                string.Equals(definition.DefinitionKey, _requestedDefinitionKey, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyRequestedPath()
    {
        if (_requestedPath is not null)
        {
            JumpToCurrentDefinitionPath(_requestedPath);
        }

        ClearRequestedRouteTarget();
    }

    private void ClearRequestedRouteTarget()
    {
        _requestedPath = null;
        _requestedDefinitionKey = null;
    }

    private Task ShowDefinitionMetadataProblemAsync(ConfigurationDefinitionSummary summary) =>
        RunOwnedAsync((route, token) => ShowDefinitionMetadataProblemAsyncCore(summary, route, token));

    private async Task ShowDefinitionMetadataProblemAsyncCore(ConfigurationDefinitionSummary summary, ConfigurationOperationLifetime route, CancellationToken token)
    {
        if (summary.MetadataDiagnostic is null)
        {
            return;
        }

        var parameters = new DialogParameters<ConfigurationDefinitionMetadataProblemDialog>();
        parameters.Add(dialog => dialog.Definition, summary);
        await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationDefinitionMetadataProblemDialog>(
            L["State:Definitions:ProblemDetailsTitle", summary.DisplayName],
            parameters,
            new DialogOptions
            {
                MaxWidth = MaxWidth.Medium,
                FullWidth = true,
                CloseButton = true
            }));
    }

    private Task SelectDefinitionAsync(ConfigurationDefinitionSummary summary) =>
        RunOwnedAsync((route, token) => SelectDefinitionAsyncCore(summary, route, token));

    private async Task SelectDefinitionAsyncCore(ConfigurationDefinitionSummary summary, ConfigurationOperationLifetime route, CancellationToken token)
    {
        if (!summary.CanManage)
        {
            return;
        }

        ClearSelectedDefinition();
        var generation = _selectionGeneration;
        var valueGeneration = _valueReadGeneration;
        _selectedSummary = summary;
        _loadingDefinition = true;
        _effectiveValues.Clear();
        _externalEffectiveSources.Clear();
        try
        {
            var result = await Facade.GetDefinitionStateAsync(summary.DefinitionKey, Visibility.ShowSensitiveValues);
            if (route.IsRetired || generation != _selectionGeneration || valueGeneration != _valueReadGeneration) return;
            if (result.IsFailed(out var error, out var definitionState))
            {
                Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
                ClearSelectedDefinition();
                return;
            }

            ApplyDefinitionState(definitionState, resetSelection: true);
            await LoadExternalEffectiveSourcesAsyncCore(route, token);
        }
        catch (Exception ex)
        {
            if (route.IsRetired || generation != _selectionGeneration || valueGeneration != _valueReadGeneration) return;
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {ex.Message}", Severity.Error);
            ClearSelectedDefinition();
        }
        finally
        {
            if (!route.IsRetired && generation == _selectionGeneration) _loadingDefinition = false;
        }
    }

    private Task LoadEffectiveValuesAsync() =>
        RunOwnedAsync((route, token) => LoadEffectiveValuesAsyncCore(route, token));

    private async Task LoadEffectiveValuesAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var definition = _selectedDefinition;
        if (definition is null)
        {
            return;
        }

        var generation = ++_valueReadGeneration;

        var result = await Facade.GetDefinitionStateAsync(definition.DefinitionKey, Visibility.ShowSensitiveValues);
        if (route.IsRetired || generation != _valueReadGeneration
            || !string.Equals(_selectedSummary?.DefinitionKey, definition.DefinitionKey, StringComparison.Ordinal)) return;
        if (result.IsFailed(out var error, out var definitionState))
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }

        ApplyDefinitionState(definitionState, resetSelection: false);
        await LoadExternalEffectiveSourcesAsyncCore(route, token);
    }

    private void ApplyDefinitionState(ConfigurationDefinitionState state, bool resetSelection)
    {
        var selectedPath = resetSelection ? LogicalPath.Root : _selectedNode?.RelativePath ?? LogicalPath.Root;
        _selectedDefinition = state.Definition;
        _startupCandidate = state.StartupCandidate;
        _isUnpersisted = state.IsUnpersisted;
        _definitionDetails[state.Definition.DefinitionKey] = state.Definition;
        _selectedNode = ResolveNodeByPath(state.Definition.Root, selectedPath) ?? state.Definition.Root;
        _effectiveValues.Clear();
        foreach (var effectiveValue in state.EffectiveValues)
        {
            _effectiveValues[effectiveValue.LogicalPath] = effectiveValue;
        }

        _externalEffectiveSources.Clear();
        if (resetSelection)
        {
            _highlightedPath = null;
        }
    }

    private Task LoadExternalEffectiveSourcesAsync() =>
        RunOwnedAsync((route, token) => LoadExternalEffectiveSourcesAsyncCore(route, token));

    private async Task LoadExternalEffectiveSourcesAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var definition = _selectedDefinition;
        var generation = _valueReadGeneration;
        if (definition?.Origin != ConfigurationDefinitionOrigin.LocalScan)
        {
            return;
        }

        var nodes = EnumerateNodes(definition.Root)
            .Where(IsComplexNodeItem)
            .ToArray();
        var result = await Facade.GetSourceChainsAsync(
            definition.DefinitionKey,
            nodes.Select(static node => node.RelativePath).ToArray(), Visibility.ShowSensitiveValues);
        if (route.IsRetired || generation != _valueReadGeneration || !ReferenceEquals(definition, _selectedDefinition)) return;
        if (result.IsFailed(out _, out var sourceChains))
        {
            return;
        }

        for (var index = 0; index < nodes.Length; index++)
        {
            var externalSource = ResolveExternalEffectiveSource(nodes[index], sourceChains[index]);
            if (externalSource is not null)
            {
                _externalEffectiveSources[nodes[index].RelativePath] = externalSource;
            }
        }
    }

    private ConfigurationSourceDescriptor? ResolveExternalEffectiveSource(
        ConfigurationNodeDefinition node,
        ConfigurationSourceChain sourceChain)
    {
        // Complex values can be composed from several providers. Prefer exact external winners from
        // already loaded scalar descendants, then fall back to the aggregate source chain for dynamic collections.
        var descendantSource = _effectiveValues
            .Where(pair => ConfigurationPathTokenizer.StartsWith(pair.Key, node.RelativePath))
            .Select(pair => pair.Value.EffectiveSource)
            .Where(IsExternalSource)
            .OrderByDescending(source => source!.PriorityIndex)
            .FirstOrDefault();

        return descendantSource
               ?? (sourceChain.Kind == ConfigurationKind.Startup ? sourceChain.StartupValues : sourceChain.Values)
                   .Where(value => value.IsEffective)
                   .Select(value => value.Source)
                   .FirstOrDefault(IsExternalSource);
    }

    private static bool IsExternalSource(ConfigurationSourceDescriptor? source)
    {
        return source is { Kind: not ConfigurationSourceKind.MonicaEffectiveStore };
    }

    private Task ReloadSelectedEffectiveValuesAndValidationAsync() =>
        RunOwnedAsync((route, token) => ReloadSelectedEffectiveValuesAndValidationAsyncCore(route, token));

    private async Task ReloadSelectedEffectiveValuesAndValidationAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        await LoadEffectiveValuesAsyncCore(route, token);
        if (route.IsRetired) return;
        await LoadRuntimeValidationReportAsyncCore(route, token);
        if (route.IsRetired) return;
        if (_reloadStatusButton is not null)
        {
            await _reloadStatusButton.RefreshAsync();
        }
    }

    private Task OnRuntimeConfigurationReloadedAsync() =>
        RunOwnedAsync((route, token) => OnRuntimeConfigurationReloadedAsyncCore(route, token));

    private async Task OnRuntimeConfigurationReloadedAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        await LoadEffectiveValuesAsyncCore(route, token);
        if (route.IsRetired) return;
        await RefreshPendingChangeBaselinesAsyncCore(route, token);
        if (route.IsRetired) return;
        await LoadRuntimeValidationReportAsyncCore(route, token);
    }

    private ConfigurationEffectiveValue? GetEffectiveValue(ConfigurationNodeDefinition node)
    {
        return _effectiveValues.GetValueOrDefault(node.RelativePath);
    }

    private ConfigurationSourceDescriptor? GetExternalEffectiveSource(ConfigurationNodeDefinition node)
    {
        return _externalEffectiveSources.GetValueOrDefault(node.RelativePath);
    }

    private Task<Res<ConfigurationDefinitionDetail>> LoadDefinitionDetailAsync(string definitionKey) =>
        RunOwnedAsync((route, token) => LoadDefinitionDetailAsyncCore(definitionKey, route, token));

    private async Task<Res<ConfigurationDefinitionDetail>> LoadDefinitionDetailAsyncCore(string definitionKey, ConfigurationOperationLifetime route, CancellationToken token)
    {
        if (_definitionDetails.TryGetValue(definitionKey, out var cachedDefinition))
        {
            return Res.Ok(new ConfigurationDefinitionDetail
            {
                Definition = cachedDefinition
            });
        }

        var result = await Facade.GetDefinitionAsync(definitionKey);
        token.ThrowIfCancellationRequested();
        if (!result.IsFailed(out _, out var detail))
        {
            _definitionDetails[definitionKey] = detail.Definition;
        }

        return result;
    }

    private static IEnumerable<ConfigurationNodeDefinition> EnumerateNodes(ConfigurationNodeDefinition node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateNodes(child))
            {
                yield return descendant;
            }
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<ConfigurationNodeSearchMatch>> BuildNodeSearchMatches()
    {
        return _definitionDetails.Values
            .Select(definition => new
            {
                definition.DefinitionKey,
                Matches = EnumerateNodes(definition.Root)
                    .Where(node => node.RelativePath.Depth > 0 && IsNodeSearchMatch(node))
                    .Select(node => new ConfigurationNodeSearchMatch
                    {
                        DefinitionKey = definition.DefinitionKey,
                        Node = node,
                        DisplayName = GetNodeLabel(node),
                        PathLabel = node.RelativePath.ToCanonicalString(),
                        Description = node.Description
                    })
                    .ToArray()
            })
            .Where(group => group.Matches.Length > 0)
            .ToDictionary(group => group.DefinitionKey, group => (IReadOnlyList<ConfigurationNodeSearchMatch>)group.Matches, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsNodeSearchMatch(ConfigurationNodeDefinition node)
    {
        return SearchTerms.Count > 0
               && (ContainsAnySearchTerm(node.Name)
                   || ContainsAnySearchTerm(node.DisplayName)
                   || ContainsAnySearchTerm(node.Description)
                   || ContainsAnySearchTerm(node.RelativePath.ToCanonicalString()));
    }

    private bool ContainsAnySearchTerm(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && SearchTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<ConfigurationNodeDefinition> EnumerateSectionNodes(ConfigurationNodeDefinition root)
    {
        yield return root;
        foreach (var node in EnumerateNodes(root).Where(node => node.RelativePath.Depth > 0 && !IsSimpleEditableNode(node)))
        {
            yield return node;
        }
    }

    private ConfigurationSectionItem BuildSectionItem(ConfigurationNodeDefinition node)
    {
        var isRoot = node.RelativePath.Depth == 0;
        var scalarCount = EnumerateNodes(node).Count(IsSimpleEditableNode);
        return new ConfigurationSectionItem(
            node,
            GetNodeLabel(node),
            isRoot ? L["State:Sections:RootDescription"].Value : node.Description,
            isRoot ? L["State:Tree:Root"].Value : node.RelativePath.ToCanonicalString(),
            SectionIcon(node),
            scalarCount);
    }

    private string GetNodeLabel(ConfigurationNodeDefinition node)
    {
        if (node.RelativePath.Depth == 0)
        {
            return L["State:Tree:Root"];
        }

        if (string.IsNullOrWhiteSpace(node.DisplayName)
            || string.Equals(node.DisplayName, node.Name, StringComparison.OrdinalIgnoreCase))
        {
            return node.Name;
        }

        return node.DisplayName!;
    }

    private static string SectionIcon(ConfigurationNodeDefinition node)
    {
        return node.NodeKind switch
        {
            ConfigurationNodeKind.Dictionary => MoIcons.Braces,
            ConfigurationNodeKind.List => MoIcons.List,
            _ when node.RelativePath.Depth == 0 => MoIcons.Home,
            _ => MoIcons.Folder
        };
    }

    private static bool IsComplexNodeItem(ConfigurationNodeDefinition node)
    {
        return !node.IsScalarCollection()
               && node.NodeKind is (ConfigurationNodeKind.Object or ConfigurationNodeKind.Dictionary or ConfigurationNodeKind.List);
    }

    private static bool IsSimpleEditableNode(ConfigurationNodeDefinition node)
    {
        return node.NodeKind == ConfigurationNodeKind.Scalar || node.IsScalarCollection();
    }

    private bool IsSelectedNode(ConfigurationNodeDefinition node)
    {
        return _selectedNode?.RelativePath.Equals(node.RelativePath) is true;
    }

    private Task OnSelectedNodeChanged(ConfigurationNodeDefinition? node)
    {
        _selectedNode = node;
        _highlightedPath = null;
        return Task.CompletedTask;
    }

    private Task OnSearchChanged(string searchText) =>
        RunOwnedAsync((route, token) => OnSearchChangedCore(searchText, route, token));

    private async Task OnSearchChangedCore(string searchText, ConfigurationOperationLifetime route, CancellationToken token)
    {
        _searchText = searchText;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            await EnsureDefinitionDetailsAsyncCore(route, token);
        }
    }

    private Task OnLifecycleFilterChangedAsync(ConfigurationDefinitionLifecycleFilter lifecycleFilter) =>
        RunOwnedAsync((route, token) => OnLifecycleFilterChangedAsyncCore(lifecycleFilter, route, token));

    private async Task OnLifecycleFilterChangedAsyncCore(ConfigurationDefinitionLifecycleFilter lifecycleFilter, ConfigurationOperationLifetime route, CancellationToken token)
    {
        _lifecycleFilter = lifecycleFilter;
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            await EnsureDefinitionDetailsAsyncCore(route, token);
        }

        if (route.IsRetired || lifecycleFilter != _lifecycleFilter) return;

        if (_selectedSummary is not null && lifecycleFilter.Includes(_selectedSummary.LifecycleState))
        {
            return;
        }

        var nextDefinition = _definitions.FirstOrDefault(definition =>
            definition.CanManage && lifecycleFilter.Includes(definition.LifecycleState));
        if (nextDefinition is null)
        {
            ClearSelectedDefinition();
            return;
        }

        await SelectDefinitionAsyncCore(nextDefinition, route, token);
    }

    private Task EnsureDefinitionDetailsAsync() =>
        RunOwnedAsync((route, token) => EnsureDefinitionDetailsAsyncCore(route, token));

    private async Task EnsureDefinitionDetailsAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var generation = _catalogGeneration;
        var filter = _lifecycleFilter;
        foreach (var summary in _definitions.ToArray())
        {
            if (!summary.CanManage
                || !filter.Includes(summary.LifecycleState)
                || _definitionDetails.ContainsKey(summary.DefinitionKey))
            {
                continue;
            }

            var result = await Facade.GetDefinitionAsync(summary.DefinitionKey);
            if (route.IsRetired || generation != _catalogGeneration || filter != _lifecycleFilter) return;
            if (!result.IsFailed(out _, out var detail))
            {
                _definitionDetails[summary.DefinitionKey] = detail.Definition;
            }
        }
    }

    private Task SelectSearchMatchAsync(ConfigurationNodeSearchMatch match) =>
        RunOwnedAsync((route, token) => SelectSearchMatchAsyncCore(match, route, token));

    private async Task SelectSearchMatchAsyncCore(ConfigurationNodeSearchMatch match, ConfigurationOperationLifetime route, CancellationToken token)
    {
        await JumpToConfigurationPathAsyncCore(match.DefinitionKey, match.Node.RelativePath, null, route, token);
    }

    private static ConfigurationNodeDefinition? ResolveNodeByPath(ConfigurationNodeDefinition? root, LogicalPath path)
    {
        return root is null
            ? null
            : EnumerateNodes(root).FirstOrDefault(node => node.RelativePath.Equals(path));
    }

    private static ConfigurationNodeDefinition? ResolveSchemaNodeByPath(ConfigurationNodeDefinition? root, LogicalPath path)
    {
        var current = root;
        foreach (var segment in path.Segments)
        {
            current = segment switch
            {
                PropertySegment property => current?.Children.FirstOrDefault(child =>
                    string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase)),
                DictionaryKeySegment => current?.DictionaryTemplate?.ValueTemplate,
                ListItemKeySegment or ListIndexSegment => current?.ListTemplate?.ItemTemplate,
                _ => null
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private Task OpenSaveGroupDialog() =>
        RunOwnedAsync((route, token) => OpenSaveGroupDialogCore(route, token));

    private async Task OpenSaveGroupDialogCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var changes = StateStore.GetPending().ToArray();
        var validationIssues = StateStore.GetValidationIssues().ToArray();
        if (await ShouldBlockSaveForRuntimeVersionDriftAsyncCore(changes, route, token))
        {
            return;
        }

        if (route.IsRetired) return;
        var parameters = new DialogParameters<SaveMutationGroupDialog>();
        parameters.Add(dialog => dialog.Changes, changes);
        parameters.Add(dialog => dialog.ValidationIssues, validationIssues);
        var store = StateStore;
        parameters.Add(dialog => dialog.Applied,
            EventCallback.Factory.Create<ConfigurationMutationGroupApplyResult>(store, applied =>
            {
                ReconcileAppliedChanges(store, changes, applied);
                return Task.CompletedTask;
            }));
        parameters.Add(
            dialog => dialog.UndoRequested,
            EventCallback.Factory.Create<ConfigurationPendingReviewUndoRequest>(store, request =>
                UndoPendingReviewAsync(request, route, store, changes, validationIssues)));
        var result = await route.ShowDialogAsync(() => DialogService.ShowAsync<SaveMutationGroupDialog>(L["Dialogs:SaveGroup:Title"], parameters, new DialogOptions
        {
            MaxWidth = MaxWidth.False,
            FullWidth = true
        }));
        if (result is { Canceled: false, Data: SaveMutationGroupDialogResult { ApplyResult: { } persisted } })
        {
            ReconcileAppliedChanges(store, changes, persisted);
        }
        if (route.IsRetired) return;
        if (result is not { Canceled: false })
        {
            return;
        }

        if (result.Data is SaveMutationGroupDialogResult { JumpChangeTarget: { } jumpChangeTarget })
        {
            await JumpToPendingChangeAsync(jumpChangeTarget);
            return;
        }

        if (result.Data is SaveMutationGroupDialogResult { JumpTarget: { } jumpTarget })
        {
            await JumpToValidationIssueAsync(jumpTarget);
            return;
        }

        if (result.Data is SaveMutationGroupDialogResult { ConcurrencyConflictMessage: { } conflictMessage })
        {
            await RefreshPendingChangeBaselinesAsyncCore(route, token);
            if (route.IsRetired) return;
            Snackbar.Add($"{L["Group:ConcurrencyConflict"]} {conflictMessage}", Severity.Warning);
            return;
        }

        if (result.Data is SaveMutationGroupDialogResult { ApplyResult: { } applyResult })
        {
            if (applyResult.Status == ConfigurationMutationGroupApplyStatus.PartiallyApplied)
            {
                await RefreshPendingChangeBaselinesAsyncCore(route, token);
                if (route.IsRetired) return;
                Snackbar.Add(L["Group:PartiallyApplied"], Severity.Warning);
            }
            else
            {
                await RefreshPendingCount();
                Snackbar.Add(L["Group:Saved"], Severity.Success);
            }

            await ReloadSelectedEffectiveValuesAndValidationAsyncCore(route, token);
            if (route.IsRetired) return;
            if (applyResult.PostCommitIssues.Count > 0)
            {
                await ShowPostCommitIssuesAsyncCore(applyResult.PostCommitIssues, route, token);
            }
        }
    }

    private Task ShowPostCommitIssuesAsync(IReadOnlyList<ConfigurationPostCommitIssue> issues) =>
        RunOwnedAsync((route, token) => ShowPostCommitIssuesAsyncCore(issues, route, token));

    private async Task ShowPostCommitIssuesAsyncCore(IReadOnlyList<ConfigurationPostCommitIssue> issues, ConfigurationOperationLifetime route, CancellationToken token)
    {
        var parameters = new DialogParameters<ConfigurationPostCommitIssuesDialog>();
        parameters.Add(dialog => dialog.Issues, issues);
        await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationPostCommitIssuesDialog>(
            L["Dialogs:PostCommitIssues:Title"],
            parameters,
            ConfigurationPostCommitIssuesDialog.DefaultOptions));
    }

    private Task UndoAll()
    {
        if (_disposed) return Task.CompletedTask;
        StateStore.Clear();
        return Task.CompletedTask;
    }

    private Task UndoPendingReviewAsync(
        ConfigurationPendingReviewUndoRequest request,
        ConfigurationOperationLifetime route,
        ConfigurationStateStore store,
        IReadOnlyList<PendingChange> changes,
        IReadOnlyList<ConfigurationValidationIssue> issues)
    {
        if (_disposed) return Task.CompletedTask;
        ReconcileReviewedUndo(route, store, changes, issues, request);
        return Task.CompletedTask;
    }

    private Task OpenImportDialog() =>
        RunOwnedAsync((route, token) => OpenImportDialogCore(route, token));

    private async Task OpenImportDialogCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var result = await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationImportDialog>(
            L["ImportExport:Import:Title"],
            new DialogOptions
            {
                MaxWidth = MaxWidth.False,
                FullWidth = true
            }));
        if (route.IsRetired) return;
        if (result is not { Canceled: false, Data: ConfigurationImportReport report })
        {
            return;
        }

        foreach (var draft in report.Drafts.Where(draft => draft.HasState))
        {
            StateStore.ReplaceScope(draft.DefinitionKey, draft.ScopePath, draft.Changes, draft.ValidationIssues);
        }

        await RefreshPendingCount();
        Snackbar.Add(L["ImportExport:Import:Staged", report.Changes.Count, report.ValidationIssues.Count], Severity.Success);
    }

    private Task OpenExportDialog(string? definitionKey, string? definitionDisplayName) =>
        RunOwnedAsync((route, token) => OpenExportDialogCore(definitionKey, definitionDisplayName, route, token));

    private async Task OpenExportDialogCore(string? definitionKey, string? definitionDisplayName, ConfigurationOperationLifetime route, CancellationToken token)
    {
        var parameters = new DialogParameters<ConfigurationExportDialog>();
        parameters.Add(dialog => dialog.DefinitionKey, definitionKey);
        parameters.Add(dialog => dialog.DefinitionDisplayName, definitionDisplayName);
        await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationExportDialog>(L["ImportExport:Export:Title"], parameters, new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true
        }));
    }

    private Task OpenSelectedDefinitionJsonEditAsync() =>
        RunOwnedAsync((route, token) => OpenSelectedDefinitionJsonEditAsyncCore(route, token));

    private async Task OpenSelectedDefinitionJsonEditAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var definition = _selectedDefinition;
        if (definition is null || !CanMutateSelectedDefinition)
        {
            return;
        }

        try
        {
            var effectiveValue = await PackageService.LoadEffectiveValueAsync(definition.DefinitionKey, LogicalPath.Root);
            if (route.IsRetired || !ReferenceEquals(definition, _selectedDefinition)) return;
            var scalarValues = await PackageService.LoadScalarEffectiveValuesAsync(
                definition,
                definition.Root,
                effectiveValue);
            if (route.IsRetired || !ReferenceEquals(definition, _selectedDefinition)) return;
            var parameters = new DialogParameters<ConfigurationJsonEditDialog>();
            parameters.Add(dialog => dialog.Definition, definition);
            parameters.Add(dialog => dialog.Node, definition.Root);
            parameters.Add(dialog => dialog.EffectiveValue, effectiveValue);
            parameters.Add(dialog => dialog.ScalarEffectiveValues, scalarValues);
            parameters.Add(dialog => dialog.PendingChanges, StateStore.GetPending());
            parameters.Add(dialog => dialog.CompactChanges, false);
            var result = await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationJsonEditDialog>(L["ImportExport:JsonEditor:Title"], parameters, new DialogOptions
            {
                MaxWidth = MaxWidth.ExtraLarge,
                FullWidth = true
            }));
            if (route.IsRetired) return;
            if (result is { Canceled: false, Data: ConfigurationJsonEditResult edit })
            {
                if (!StateStore.TryReplaceJsonEditorScope(edit))
                {
                    Snackbar.Add(L["ImportExport:JsonEditor:PendingStateUnavailable"], Severity.Warning);
                    return;
                }
                await RefreshPendingCount();
            }
        }
        catch (Exception ex)
        {
            if (route.IsRetired) return;
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {ex.Message}", Severity.Error);
        }
    }

    private Task OpenSelectedDefinitionHistoryAsync() =>
        RunOwnedAsync((route, token) => OpenSelectedDefinitionHistoryAsyncCore(route, token));

    private async Task OpenSelectedDefinitionHistoryAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var definition = _selectedDefinition;
        var allowRollback = CanMutateSelectedDefinition;
        var definitions = _definitions.ToArray();
        if (definition is null)
        {
            return;
        }

        var request = new ConfigurationHistoryPageRequest
        {
            DefinitionKey = definition.DefinitionKey
        };
        var result = await Facade.QueryHistoryPageAsync(request);
        if (route.IsRetired || !ReferenceEquals(definition, _selectedDefinition)) return;
        if (result.IsFailed(out var error, out var page))
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }

        var parameters = new DialogParameters<ConfigurationHistoryDiffDialog>();
        parameters.Add(dialog => dialog.HistoryRequest, request);
        parameters.Add(dialog => dialog.InitialPage, page);
        parameters.Add(dialog => dialog.Definitions, definitions);
        parameters.Add(dialog => dialog.Definition, definition);
        parameters.Add(dialog => dialog.AllowRollback, allowRollback);
        parameters.Add(dialog => dialog.Title, L["History:Dialog:MutationTitle", definition.DisplayName]);
        var dialogResult = await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationHistoryDiffDialog>(L["History:Dialog:Title"], parameters, new DialogOptions
        {
            MaxWidth = MaxWidth.ExtraLarge,
            FullWidth = true
        }));
        if (route.IsRetired) return;
        if (dialogResult is { Canceled: false, Data: ConfigurationHistoryRollbackDialogResult rollback })
        {
            Snackbar.Add(L[rollback.PostCommitIssues.Count == 0 ? "Rollback:Success" : "Rollback:AppliedWithIssues"],
                rollback.PostCommitIssues.Count == 0 ? Severity.Success : Severity.Warning);
            if (rollback.PostCommitIssues.Count > 0)
            {
                await ShowPostCommitIssuesAsyncCore(rollback.PostCommitIssues, route, token);
            }
            if (route.IsRetired) return;
            await LoadEffectiveValuesAsyncCore(route, token);
            if (route.IsRetired) return;
            await RefreshPendingCount();
        }
    }

    private Task OpenSelectedDefinitionPublicationAsync(ConfigurationDefinitionPublicationTab initialTab) =>
        RunOwnedAsync((route, token) => OpenSelectedDefinitionPublicationAsyncCore(initialTab, route, token));

    private async Task OpenSelectedDefinitionPublicationAsyncCore(ConfigurationDefinitionPublicationTab initialTab, ConfigurationOperationLifetime route, CancellationToken token)
    {
        var definition = _selectedDefinition;
        if (definition is null)
        {
            return;
        }

        var result = await Facade.GetDefinitionPublicationOverviewAsync(definition.DefinitionKey, 50);
        if (route.IsRetired || !ReferenceEquals(definition, _selectedDefinition)) return;
        if (result.IsFailed(out var error, out var overview))
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }

        var parameters = new DialogParameters<ConfigurationDefinitionPublicationDialog>();
        parameters.Add(dialog => dialog.Definition, definition);
        parameters.Add(dialog => dialog.Overview, overview);
        parameters.Add(dialog => dialog.InitialTab, initialTab);
        await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationDefinitionPublicationDialog>(
            L["DefinitionPublication:Title", definition.DisplayName],
            parameters,
            new DialogOptions
            {
                MaxWidth = MaxWidth.ExtraLarge,
                FullWidth = true
            }));
    }

    private Task OpenSelectedDefinitionPurgeAsync() =>
        RunOwnedAsync((route, token) => OpenSelectedDefinitionPurgeAsyncCore(route, token));

    private async Task OpenSelectedDefinitionPurgeAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        if (_selectedSummary is null)
        {
            return;
        }

        await OpenDefinitionPurgeAsyncCore(_selectedSummary, route, token);
    }

    private Task OpenDefinitionPurgeAsync(ConfigurationDefinitionSummary summary) =>
        RunOwnedAsync((route, token) => OpenDefinitionPurgeAsyncCore(summary, route, token));

    private async Task OpenDefinitionPurgeAsyncCore(ConfigurationDefinitionSummary summary, ConfigurationOperationLifetime route, CancellationToken token)
    {
        if (summary.LifecycleState != ConfigurationDefinitionLifecycleState.Retired)
        {
            return;
        }

        var previewResult = await Facade.PreviewDefinitionPurgeAsync(summary.DefinitionKey);
        if (route.IsRetired) return;
        if (previewResult.IsFailed(out var previewError, out var preview))
        {
            Snackbar.Add(
                L["DefinitionPurge:Messages:PreviewFailed", previewError.Message ?? string.Empty],
                Severity.Error);
            return;
        }

        var parameters = new DialogParameters<ConfigurationDefinitionPurgeDialog>();
        parameters.Add(dialog => dialog.Preview, preview);
        var dialogResult = await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationDefinitionPurgeDialog>(
            L["DefinitionPurge:Title", preview.DisplayName],
            parameters,
            new DialogOptions
            {
                MaxWidth = MaxWidth.Medium,
                FullWidth = true,
                CloseButton = true
            }));
        if (route.IsRetired) return;
        if (dialogResult is not { Canceled: false, Data: ConfigurationDefinitionPurgeRequest request })
        {
            return;
        }

        var purgeResult = await Facade.PurgeDefinitionAsync(request);
        if (!purgeResult.IsFailed(out _)) StateStore.UndoScope(request.DefinitionKey, LogicalPath.Root);
        if (route.IsRetired) return;
        if (purgeResult.IsFailed(out var purgeError))
        {
            Snackbar.Add(
                L["DefinitionPurge:Messages:PurgeFailed", purgeError.Message ?? string.Empty],
                Severity.Error);
            return;
        }

        Snackbar.Add(L["DefinitionPurge:Messages:Succeeded", preview.DisplayName], Severity.Success);
        await LoadDefinitionsAsyncCore(route, token);
    }

    private Task OpenDefinitionSourcesAsync() =>
        RunOwnedAsync((route, token) => OpenDefinitionSourcesAsyncCore(route, token));

    private async Task OpenDefinitionSourcesAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var definition = _selectedDefinition;
        if (definition is null)
        {
            return;
        }

        var parameters = new DialogParameters<ConfigurationDefinitionSourcesDialog>();
        parameters.Add(dialog => dialog.Definition, definition);
        await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationDefinitionSourcesDialog>(L["Sources:Definition:Title"], parameters, new DialogOptions
        {
            MaxWidth = MaxWidth.ExtraLarge,
            FullWidth = true,
            CloseButton = true,
            CloseOnEscapeKey = true
        }), action => InvokeAsync(action));
    }

    private Task OpenRuntimeIssueSourceChainAsync(ConfigurationValidationIssue issue) =>
        RunOwnedAsync((route, token) => OpenRuntimeIssueSourceChainAsyncCore(issue, route, token));

    private async Task OpenRuntimeIssueSourceChainAsyncCore(ConfigurationValidationIssue issue, ConfigurationOperationLifetime route, CancellationToken token)
    {
        CloseRuntimeValidationPopover();
        var result = await LoadDefinitionDetailAsyncCore(issue.DefinitionKey, route, token);
        if (route.IsRetired) return;
        if (result.IsFailed(out var error, out var detail))
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }

        var node = ResolveSchemaNodeByPath(detail.Definition.Root, issue.LogicalPath)
                   ?? ResolveNodeByPathOrNearestAncestor(detail.Definition.Root, issue.LogicalPath);
        if (node is null)
        {
            Snackbar.Add(L["State:RuntimeValidation:SourceChainUnavailable"], Severity.Error);
            return;
        }

        var parameters = new DialogParameters<ConfigurationSourceChainDialog>();
        parameters.Add(dialog => dialog.Definition, detail.Definition);
        parameters.Add(dialog => dialog.Node, node);
        parameters.Add(dialog => dialog.LogicalPath, issue.LogicalPath);
        parameters.Add(dialog => dialog.AllowSourceEdit, false);
        await route.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationSourceChainDialog>(L["Sources:Chain:Title"], parameters, new DialogOptions
        {
            MaxWidth = MaxWidth.ExtraLarge,
            FullWidth = true
        }));
    }

    private Task RefreshPendingCount()
    {
        _pendingCount = StateStore.GetPending().Count;
        _validationIssueCount = StateStore.GetValidationIssues().Count;
        return Task.CompletedTask;
    }

    private Task RefreshPendingChangeBaselinesAsync() =>
        RunOwnedAsync((route, token) => RefreshPendingChangeBaselinesAsyncCore(route, token));

    private async Task RefreshPendingChangeBaselinesAsyncCore(ConfigurationOperationLifetime route, CancellationToken token)
    {
        var pendingChanges = StateStore.GetPending();
        if (pendingChanges.Count == 0)
        {
            return;
        }

        var refreshedChanges = new List<PendingChange>(pendingChanges.Count);
        foreach (var definitionChanges in pendingChanges.GroupBy(
                     static change => change.DefinitionKey,
                     StringComparer.OrdinalIgnoreCase))
        {
            var result = await LoadDefinitionDetailAsyncCore(definitionChanges.Key, route, token);
            if (route.IsRetired) return;
            if (result.IsFailed(out _, out var detail))
            {
                refreshedChanges.AddRange(definitionChanges);
                continue;
            }

            var rootValueResult = await Facade.GetEffectiveValueAsync(definitionChanges.Key, LogicalPath.Root, Visibility.ShowSensitiveValues);
            if (route.IsRetired) return;
            if (rootValueResult.IsFailed(out _, out var rootValue))
            {
                refreshedChanges.AddRange(definitionChanges);
                continue;
            }

            // One persisted document version guards every Monica mutation for this definition.
            foreach (var change in definitionChanges)
            {
                var schema = ResolveSchemaNodeByPath(detail.Definition.Root, change.LogicalPath);
                if (schema is null)
                {
                    refreshedChanges.Add(change);
                    continue;
                }

                var effectiveValueResult = await Facade.GetEffectiveValueAsync(change.DefinitionKey, change.LogicalPath, Visibility.ShowSensitiveValues);
                if (route.IsRetired) return;
                if (effectiveValueResult.IsFailed(out _, out var effectiveValue))
                {
                    refreshedChanges.Add(change);
                    continue;
                }

                var refreshedChange = change.RebaseBaseline(
                    detail.Definition,
                    schema,
                    effectiveValue,
                    rootValue.Version,
                    L);
                if (!refreshedChange.HasSameOriginalAndNewValue())
                {
                    refreshedChanges.Add(refreshedChange);
                }
            }
        }

        StateStore.ReplacePending(MergeRefreshedPendingChanges(pendingChanges, refreshedChanges, StateStore.GetPending()));
        await RefreshPendingCount();
    }

    private Task<bool> ShouldBlockSaveForRuntimeVersionDriftAsync(IReadOnlyList<PendingChange> changes) =>
        RunOwnedAsync((route, token) => ShouldBlockSaveForRuntimeVersionDriftAsyncCore(changes, route, token));

    private async Task<bool> ShouldBlockSaveForRuntimeVersionDriftAsyncCore(IReadOnlyList<PendingChange> changes, ConfigurationOperationLifetime route, CancellationToken token)
    {
        var monicaDefinitionKeys = changes
            .Where(static change => change.TargetKind == ConfigurationMutationTargetKind.MonicaEffectiveStore)
            .Select(static change => change.DefinitionKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (monicaDefinitionKeys.Count == 0)
        {
            return false;
        }

        var statusResult = await Facade.GetRuntimeReloadStatusAsync();
        if (route.IsRetired) return true;
        if (statusResult.IsFailed(out var error, out var report))
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return true;
        }

        var driftedDefinitions = report.Definitions
            .Where(definition =>
                monicaDefinitionKeys.Contains(definition.DefinitionKey)
                && definition.RuntimeLoadedVersion != definition.EffectiveStoreVersion)
            .OrderBy(static definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (driftedDefinitions.Length == 0)
        {
            return false;
        }

        var details = string.Join("; ", driftedDefinitions.Select(FormatSaveBlockedReloadDefinition));
        Snackbar.Add(L["State:ReloadStatus:SaveBlockedPendingDefinitions", details], Severity.Warning);
        if (_reloadStatusButton is not null)
        {
            await _reloadStatusButton.OpenAsync(report);
        }

        return true;
    }

    private string FormatSaveBlockedReloadDefinition(ConfigurationDefinitionReloadStatus definition)
    {
        return L["State:ReloadStatus:SaveBlockedDefinitionVersion",
            definition.DisplayName,
            FormatReloadVersion(definition.RuntimeLoadedVersion),
            FormatReloadVersion(definition.EffectiveStoreVersion)].Value;
    }

    private string FormatReloadVersion(long? version)
    {
        return version is null ? L["State:ReloadStatus:UnknownValue"].Value : L["State:ReloadStatus:VersionValue", version.Value].Value;
    }

    private void OnStateStoreChanged()
    {
        if (_disposed)
        {
            return;
        }

        var route = _routeLifetime;
        _pendingStateRender = ObserveStateRenderAsync(route, route.RunAsync(token => InvokeAsync(() =>
        {
            if (!route.IsRetired && !_disposed)
            {
                _pendingCount = StateStore.GetPending().Count;
                _validationIssueCount = StateStore.GetValidationIssues().Count;
                StateHasChanged();
            }
        })));
    }

    private async Task ObserveStateRenderAsync(ConfigurationOperationLifetime route, Task render)
    {
        try { await render; }
        catch (OperationCanceledException) when (route.IsRetired) { }
        catch (Exception exception) { await DispatchExceptionAsync(exception); }
    }

    /// <summary>Removes only still-current captured drafts whose actual persistence outcome applied.</summary>
    internal static void ReconcileAppliedChanges(
        ConfigurationStateStore store, IReadOnlyList<PendingChange> captured, ConfigurationMutationGroupApplyResult result)
    {
        var applied = result.Outcomes.Where(outcome => outcome.Status == ConfigurationMutationOutcomeStatus.Applied)
            .Select(outcome => outcome.RequestId).ToHashSet(StringComparer.Ordinal);
        foreach (var change in captured)
        {
            if (applied.Contains(change.RequestId) && Equals(store.Get(change.DefinitionKey, change.LogicalPath), change))
            {
                store.UndoChange(change.DefinitionKey, change.LogicalPath);
            }
        }
    }

    /// <summary>Undoes only still-current items captured by a live route's review dialog.</summary>
    internal static void ReconcileReviewedUndo(
        ConfigurationOperationLifetime route,
        ConfigurationStateStore store,
        IReadOnlyList<PendingChange> capturedChanges,
        IReadOnlyList<ConfigurationValidationIssue> capturedIssues,
        ConfigurationPendingReviewUndoRequest request)
    {
        if (route.IsRetired) return;
        if (request.Target is ConfigurationPendingReviewUndoTarget.Change or ConfigurationPendingReviewUndoTarget.Definition)
        {
            foreach (var change in capturedChanges)
            {
                if (route.IsRetired) return;
                if (MatchesReviewTarget(change.DefinitionKey, change.LogicalPath, request)
                    && Equals(store.Get(change.DefinitionKey, change.LogicalPath), change))
                {
                    store.UndoChange(change.DefinitionKey, change.LogicalPath);
                }
            }
        }
        if (request.Target is ConfigurationPendingReviewUndoTarget.ValidationIssue or ConfigurationPendingReviewUndoTarget.Definition)
        {
            foreach (var issue in capturedIssues)
            {
                if (route.IsRetired) return;
                if (MatchesReviewTarget(issue.DefinitionKey, issue.LogicalPath, request)
                    && Equals(store.GetValidationIssue(issue.DefinitionKey, issue.LogicalPath), issue))
                {
                    store.ClearValidationIssue(issue.DefinitionKey, issue.LogicalPath);
                }
            }
        }
    }

    private static bool MatchesReviewTarget(string definitionKey, LogicalPath path, ConfigurationPendingReviewUndoRequest request) =>
        string.Equals(definitionKey, request.DefinitionKey, StringComparison.Ordinal)
        && (request.Target == ConfigurationPendingReviewUndoTarget.Definition || path.Equals(request.LogicalPath));

    /// <summary>Merges refreshed baselines only where the draft admitted by that read remains current.</summary>
    internal static IReadOnlyList<PendingChange> MergeRefreshedPendingChanges(
        IReadOnlyList<PendingChange> captured, IReadOnlyList<PendingChange> refreshed, IReadOnlyList<PendingChange> current)
    {
        var originals = captured.ToDictionary(change => change.RequestId, StringComparer.Ordinal);
        var replacements = refreshed.ToDictionary(change => change.RequestId, StringComparer.Ordinal);
        var merged = new List<PendingChange>(current.Count);
        foreach (var change in current)
        {
            if (!originals.TryGetValue(change.RequestId, out var original) || !Equals(original, change))
            {
                merged.Add(change);
            }
            else if (replacements.TryGetValue(change.RequestId, out var replacement))
            {
                merged.Add(replacement);
            }
        }
        return merged;
    }

    private Task JumpToPendingChangeAsync(PendingChange change)
    {
        return JumpToConfigurationPathAsync(
            change.DefinitionKey,
            change.LogicalPath,
            L["Dialogs:SaveGroup:Actions:JumpFailed"].Value);
    }

    private Task JumpToValidationIssueAsync(ConfigurationValidationIssue issue)
    {
        return JumpToConfigurationPathAsync(
            issue.DefinitionKey,
            issue.LogicalPath,
            L["Dialogs:SaveGroup:Validation:JumpFailed"].Value);
    }

    private Task JumpToRuntimeValidationIssueAsync(ConfigurationValidationIssue issue)
    {
        CloseRuntimeValidationPopover();
        return JumpToConfigurationPathAsync(
            issue.DefinitionKey,
            issue.LogicalPath,
            L["State:RuntimeValidation:JumpFailed"].Value);
    }

    private Task JumpToConfigurationPathAsync(string definitionKey, LogicalPath path, string? failureMessage) =>
        RunOwnedAsync((route, token) => JumpToConfigurationPathAsyncCore(definitionKey, path, failureMessage, route, token));

    private async Task JumpToConfigurationPathAsyncCore(string definitionKey, LogicalPath path, string? failureMessage, ConfigurationOperationLifetime route, CancellationToken token)
    {
        var summary = _definitions.FirstOrDefault(definition =>
            string.Equals(definition.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase));
        if (summary is null || !summary.CanManage)
        {
            ShowJumpFailure(failureMessage);
            return;
        }

        await SelectDefinitionAsyncCore(summary, route, token);
        if (route.IsRetired || !string.Equals(_selectedDefinition?.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase)) return;
        if (!JumpToCurrentDefinitionPath(path))
        {
            ShowJumpFailure(failureMessage);
        }
    }

    private bool JumpToCurrentDefinitionPath(LogicalPath path)
    {
        var root = _selectedDefinition?.Root;
        if (root is null)
        {
            return false;
        }

        var target = ResolveNodeByPathOrNearestAncestor(root, path);
        if (target is null)
        {
            return false;
        }

        _selectedNode = ResolveDisplaySectionForTarget(root, target) ?? root;
        _highlightedPath = target.RelativePath;
        return true;
    }

    private static ConfigurationNodeDefinition? ResolveDisplaySectionForTarget(
        ConfigurationNodeDefinition root,
        ConfigurationNodeDefinition target)
    {
        if (target.RelativePath.Depth == 0 || !IsSimpleEditableNode(target))
        {
            return target;
        }

        for (var depth = target.RelativePath.Depth - 1; depth >= 0; depth--)
        {
            var candidatePath = new LogicalPath(target.RelativePath.Segments.Take(depth).ToArray());
            var candidate = ResolveNodeByPath(root, candidatePath);
            if (candidate is not null && !IsSimpleEditableNode(candidate))
            {
                return candidate;
            }
        }

        return root;
    }

    private static ConfigurationNodeDefinition? ResolveNodeByPathOrNearestAncestor(ConfigurationNodeDefinition? root, LogicalPath path)
    {
        if (root is null)
        {
            return null;
        }

        for (var depth = path.Depth; depth >= 0; depth--)
        {
            var candidatePath = new LogicalPath(path.Segments.Take(depth).ToArray());
            var candidate = ResolveNodeByPath(root, candidatePath);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return root;
    }

    private void ShowJumpFailure(string? failureMessage)
    {
        if (!string.IsNullOrWhiteSpace(failureMessage))
        {
            Snackbar.Add(failureMessage, Severity.Error);
        }
    }

    private sealed record ConfigurationSectionItem(
        ConfigurationNodeDefinition Node,
        string Title,
        string? Description,
        string PathLabel,
        string Icon,
        int ScalarCount);
}
