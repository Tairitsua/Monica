using Microsoft.AspNetCore.Components;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Dialogs;
using Monica.Configuration.UI.Models;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;
using Monica.UI.Icons;
using MudBlazor;

namespace Monica.Configuration.UI.Pages;

/// <summary>Owns configuration History reads, cursor scope, and native inspection and rollback dialogs.</summary>
/// <remarks>Filters are applied only by Search or Refresh. Retirement drains admitted reads and actual native
/// results, rejects stale visible adoption, and cannot reverse a persisted rollback. Facade reads are tokenless;
/// retirement waits for their actual completion rather than claiming that cancellation stopped the backend.</remarks>
public partial class ConfigurationHistoryPage
{

    /// <summary>Gets the Configuration History route.</summary>
    public const string PAGE_URL = ConfigurationUiRoutes.HISTORY_ROUTE;
    private const string TARGET_FILTER_ALL = "all";
    private const string TARGET_FILTER_MONICA = "monica";
    private const string TARGET_FILTER_EXTERNAL = "external";
    private const int PAGE_SIZE = 50;

    private readonly List<ConfigurationDefinitionSummary> _definitions = [];
    private readonly Dictionary<string, ConfigurationDefinition> _definitionDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConfigurationValueHistory> _history = [];
    private readonly List<ConfigurationMutationGroup> _groups = [];
    private IReadOnlyList<ConfigurationHistoryDisplayRow> _displayHistory = [];
    private ConfigurationHistoryPageRequest? _activeHistoryRequest;
    private ConfigurationMutationGroupPageRequest? _activeGroupRequest;
    private string? _definitionKey;
    private string _pathText = string.Empty;
    private string _targetFilter = TARGET_FILTER_ALL;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private bool _loading = true;
    private bool _loadingMoreHistory;
    private bool _loadingMoreGroups;
    private bool _historyHasMore;
    private bool _groupsHaveMore;
    private ConfigurationHistoryCursor? _historyNextCursor;
    private ConfigurationMutationGroupCursor? _groupsNextCursor;
    private ConfigurationMetadataStoreDiagnostic? _metadataStoreDiagnostic;

    private ConfigurationOperationLifetime _operations = new();
    private readonly List<ConfigurationOperationLifetime> _routeOwners = [];
    private readonly object _retirementGate = new();
    private readonly ConfigurationHistoryQuerySession _queries = new();
    private ConfigurationHistoryQuerySession.Query? _activeQuery;
    private (string? DefinitionKey, string? Path)? _routeIdentity;
    private TaskCompletionSource? _retirement;
    private ConfigurationOperationLifetime? _dialogAdmission;
    private bool _disposed;
    private bool _hasLoadedHistory;
    private bool _hasLoadedGroups;
    private string? _historyLoadError;
    private string? _groupLoadError;
    private ConfigurationHistoryPageRequest? _loadedHistoryRequest;
    private ConfigurationMutationGroupPageRequest? _loadedGroupRequest;

    /// <summary>Gets the exact definition identity supplied by History-route navigation.</summary>
    [SupplyParameterFromQuery(Name = "definitionKey")]
    public string? RouteDefinitionKey { get; set; }

    /// <summary>Gets the logical path query, which is validated before a history load is admitted.</summary>
    [SupplyParameterFromQuery(Name = "path")]
    public string? RoutePath { get; set; }

    protected override async Task OnParametersSetAsync()
    {
        ConfigurationOperationLifetime? previous;
        ConfigurationOperationLifetime owner;
        lock (_retirementGate)
        {
            if (_disposed || _routeIdentity == (RouteDefinitionKey, RoutePath)) return;
            previous = _routeIdentity is null ? null : _operations;
            _routeIdentity = (RouteDefinitionKey, RoutePath);
            owner = previous is null ? _operations : new ConfigurationOperationLifetime();
            _operations = owner;
            _routeOwners.Add(owner);
        }
        _queries.Invalidate();
        ClearQueryEvidence();
        _definitionKey = RouteDefinitionKey;
        _pathText = RoutePath ?? string.Empty;
        _loading = true;
        if (previous is not null) await previous.RetireAsync();
        if (_disposed || owner.IsRetired || !ReferenceEquals(owner, _operations)) return;
        await ReloadAsync();
    }

    /// <summary>Closes route-owned dialogs and joins the actual admitted reads and dialog outcomes.</summary>
    /// <remarks>Repeated callers join the same completion. A persisted rollback cannot be cancelled or reversed
    /// by retirement; its dialog independently owns and drains its tokenless persistence producer.</remarks>
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
            owners = _routeOwners.Append(_operations).Distinct().ToArray();
        }
        _queries.Invalidate();
        _ = CompleteRetirementAsync(owners, completion);
        return new(completion.Task);
    }

    private static async Task CompleteRetirementAsync(ConfigurationOperationLifetime[] owners, TaskCompletionSource completion)
    {
        var retirements = owners.Select(owner => owner.RetireAsync().AsTask()).ToArray();
        var failures = new List<Exception>();
        foreach (var retirement in retirements)
        {
            try { await retirement; }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Configuration History retirement failed.", failures));
    }

    private async Task RunOwnedAsync(Func<ConfigurationOperationLifetime, CancellationToken, Task> producer)
    {
        var owner = _operations;
        if (_disposed || owner.IsRetired) return;
        try { await owner.RunAsync(token => producer(owner, token)); }
        catch (OperationCanceledException) when (owner.IsRetired) { }
    }

    private async Task RunDialogAsync(Func<ConfigurationOperationLifetime, CancellationToken, Task> producer)
    {
        var owner = _operations;
        if (_disposed || owner.IsRetired || ReferenceEquals(_dialogAdmission, owner)) return;
        _dialogAdmission = owner;
        try { await owner.RunAsync(token => producer(owner, token)); }
        catch (OperationCanceledException) when (owner.IsRetired) { }
        finally { if (ReferenceEquals(_dialogAdmission, owner)) _dialogAdmission = null; }
    }

    private bool CanAdopt(ConfigurationOperationLifetime owner, ConfigurationHistoryQuerySession.Query query) =>
        !_disposed && !owner.IsRetired && _queries.IsCurrent(query);

    private void ClearQueryEvidence()
    {
        _activeQuery = null;
        _activeHistoryRequest = null;
        _activeGroupRequest = null;
        _loadedHistoryRequest = null;
        _loadedGroupRequest = null;
        _history.Clear();
        _displayHistory = [];
        _groups.Clear();
        _historyHasMore = false;
        _groupsHaveMore = false;
        _historyNextCursor = null;
        _groupsNextCursor = null;
        _loadingMoreHistory = false;
        _loadingMoreGroups = false;
        _hasLoadedHistory = false;
        _hasLoadedGroups = false;
        _historyLoadError = null;
        _groupLoadError = null;
    }

    /// <summary>Freezes the edited filters at Search/Refresh admission. Invalid paths never become broad queries.</summary>
    internal static (ConfigurationHistoryPageRequest History, ConfigurationMutationGroupPageRequest Groups) CreateRequests(
        string? definitionKey, string pathText, string targetFilter, DateTime? fromDate, DateTime? toDate,
        TimeZoneInfo timeZone)
    {
        var path = string.IsNullOrWhiteSpace(pathText) ? null : LogicalPath.Parse(pathText);
        var (from, to) = ConfigurationCalendarRange.Create(fromDate, toDate, timeZone);
        return (new ConfigurationHistoryPageRequest
        {
            From = from, To = to, DefinitionKey = definitionKey, LogicalPath = path,
            TargetKind = targetFilter switch
            {
                TARGET_FILTER_MONICA => ConfigurationMutationTargetKind.MonicaEffectiveStore,
                TARGET_FILTER_EXTERNAL => ConfigurationMutationTargetKind.ExternalConfigurationSource,
                _ => null
            },
            PageSize = PAGE_SIZE
        }, new ConfigurationMutationGroupPageRequest
        {
            From = from, To = to, DefinitionKey = definitionKey, PageSize = PAGE_SIZE
        });
    }

    private ConfigurationHistoryQuerySession.Query? BeginQuery()
    {
        _queries.Invalidate();
        ClearQueryEvidence();
        try
        {
            var requests = CreateRequests(_definitionKey, _pathText, _targetFilter, _fromDate, _toDate, TimeDisplay.TimeZone);
            if (requests.History.LogicalPath is { } path) _pathText = path.ToCanonicalString();
            var query = _queries.Begin(requests.History, requests.Groups);
            _activeQuery = query;
            _activeHistoryRequest = query.History;
            _activeGroupRequest = query.Groups;
            _loading = true;
            return query;
        }
        catch (Exception exception)
        {
            var message = exception switch
            {
                ConfigurationCalendarRangeException { Error: ConfigurationCalendarRangeError.Reversed } => L["History:Filters:ReversedDates"],
                ConfigurationCalendarRangeException { Error: ConfigurationCalendarRangeError.MissingBoundary } => L["History:Filters:MissingBoundary"],
                ConfigurationCalendarRangeException => L["History:Filters:RepeatedBoundary"],
                ConfigurationPathFormatException => L["Common:Errors:InvalidPath"],
                _ => L["Common:Errors:LoadFailed"]
            };
            _historyLoadError = exception is ConfigurationCalendarRangeException ? message.Value : $"{message}: {exception.Message}";
            _groupLoadError = _historyLoadError;
            _loading = false;
            Snackbar.Add(_historyLoadError, Severity.Error);
            return null;
        }
    }

    private async Task LoadDefinitionsCoreAsync(ConfigurationOperationLifetime owner, ConfigurationHistoryQuerySession.Query query)
    {
        var result = await Facade.GetDefinitionCatalogAsync();
        if (!CanAdopt(owner, query)) return;
        if (result.IsFailed(out var error, out var catalog))
        {
            _queries.TryApply(query, () => { _definitions.Clear(); _definitionDetails.Clear(); _metadataStoreDiagnostic = null; });
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }
        _queries.TryApply(query, () =>
        {
            _definitions.Clear();
            _definitions.AddRange(catalog.Definitions);
            _definitionDetails.Clear();
            _metadataStoreDiagnostic = catalog.StoreDiagnostic;
        });
    }

    // Refresh applies the edited filters too; it freezes them before the catalog producer can yield.
    private Task ReloadAsync() => RunOwnedAsync(async (owner, token) =>
    {
        var query = BeginQuery();
        if (query is null) return;
        try
        {
            await LoadDefinitionsCoreAsync(owner, query);
            if (CanAdopt(owner, query)) await LoadQueryCoreAsync(owner, query);
        }
        finally { if (CanAdopt(owner, query)) _loading = false; }
    });

    private Task LoadAsync() => RunOwnedAsync(async (owner, token) =>
    {
        var query = BeginQuery();
        if (query is not null) await LoadQueryCoreAsync(owner, query);
    });

    private async Task LoadQueryCoreAsync(ConfigurationOperationLifetime owner, ConfigurationHistoryQuerySession.Query query)
    {
        try
        {
            await LoadHistoryPageCoreAsync(query.History, append: false, owner, query);
            if (CanAdopt(owner, query)) await LoadGroupPageCoreAsync(query.Groups, append: false, owner, query);
        }
        finally { if (CanAdopt(owner, query)) _loading = false; }
    }

    private async Task LoadHistoryPageCoreAsync(ConfigurationHistoryPageRequest request, bool append,
        ConfigurationOperationLifetime owner, ConfigurationHistoryQuerySession.Query query)
    {
        var result = await Facade.QueryHistoryPageAsync(request);
        if (!CanAdopt(owner, query)) return;
        if (result.IsFailed(out var error, out var page))
        {
            _queries.TryApply(query, () =>
            {
                _historyLoadError = error.Message ?? L["Common:Errors:LoadFailed"].Value;
                if (!append) { _history.Clear(); _displayHistory = []; _hasLoadedHistory = false; _historyHasMore = false; _historyNextCursor = null; }
            });
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }
        _queries.TryApply(query, () =>
        {
            if (!append) _history.Clear();
            var loadedIds = _history.Select(static history => history.HistoryId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _history.AddRange(page.Items.Where(history => loadedIds.Add(history.HistoryId)));
            _displayHistory = ConfigurationHistoryDisplayGrouper.Collapse(_history);
            _historyHasMore = page.HasMore;
            _historyNextCursor = page.NextCursor;
            _hasLoadedHistory = true;
            _historyLoadError = null;
            _loadedHistoryRequest = query.History;
        });
        await EnsureDefinitionDetailsCoreAsync(page.Items.Select(static row => row.DefinitionKey), owner, query);
    }

    private async Task LoadGroupPageCoreAsync(ConfigurationMutationGroupPageRequest request, bool append,
        ConfigurationOperationLifetime owner, ConfigurationHistoryQuerySession.Query query)
    {
        var result = await Facade.QueryMutationGroupsPageAsync(request);
        if (!CanAdopt(owner, query)) return;
        if (result.IsFailed(out var error, out var page))
        {
            _queries.TryApply(query, () =>
            {
                _groupLoadError = error.Message ?? L["Common:Errors:LoadFailed"].Value;
                if (!append) { _groups.Clear(); _hasLoadedGroups = false; _groupsHaveMore = false; _groupsNextCursor = null; }
            });
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error);
            return;
        }
        _queries.TryApply(query, () =>
        {
            if (!append) _groups.Clear();
            var loadedIds = _groups.Select(static group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _groups.AddRange(page.Items.Where(group => loadedIds.Add(group.GroupId)));
            _groupsHaveMore = page.HasMore;
            _groupsNextCursor = page.NextCursor;
            _hasLoadedGroups = true;
            _groupLoadError = null;
            _loadedGroupRequest = query.Groups;
        });
        await EnsureDefinitionDetailsCoreAsync(page.Items.SelectMany(static group => group.DefinitionKeys), owner, query);
    }

    private Task LoadMoreHistoryAsync() => RunOwnedAsync(async (owner, token) =>
    {
        var query = _activeQuery;
        if (_loading || _loadingMoreHistory || !_historyHasMore || query is null || _activeHistoryRequest is null || _historyNextCursor is null) return;
        var request = _activeHistoryRequest with { Cursor = _historyNextCursor };
        _loadingMoreHistory = true;
        try { await LoadHistoryPageCoreAsync(request, append: true, owner, query); }
        finally { if (CanAdopt(owner, query)) _loadingMoreHistory = false; }
    });

    private Task LoadMoreGroupsAsync() => RunOwnedAsync(async (owner, token) =>
    {
        var query = _activeQuery;
        if (_loading || _loadingMoreGroups || !_groupsHaveMore || query is null || _activeGroupRequest is null || _groupsNextCursor is null) return;
        var request = _activeGroupRequest with { Cursor = _groupsNextCursor };
        _loadingMoreGroups = true;
        try { await LoadGroupPageCoreAsync(request, append: true, owner, query); }
        finally { if (CanAdopt(owner, query)) _loadingMoreGroups = false; }
    });

    private Task RollbackHistoryAsync(ConfigurationHistoryDisplayRow history) => RunDialogAsync(async (owner, token) =>
    {
        var rows = history.Rows.ToArray();
        if (!rows.All(CanRollbackRow)) { Snackbar.Add(L["History:Actions:RollbackBlockedMetadata"], Severity.Warning); return; }
        var parameters = new DialogParameters<RollbackConfirmationDialog>();
        if (rows.Length == 1) parameters.Add(dialog => dialog.HistoryRow, history.Representative);
        parameters.Add(dialog => dialog.GroupRows, rows);
        var title = rows.Length == 1 ? L["Rollback:SingleTitle"] : L["Rollback:SelectionTitle"];
        var result = await owner.ShowDialogAsync(() => DialogService.ShowAsync<RollbackConfirmationDialog>(title, parameters, EditorOptions));
        await HandleRollbackResultAsync(result, owner);
    });

    private Task RollbackGroupAsync(ConfigurationMutationGroup group) => RunDialogAsync(async (owner, token) =>
    {
        if (!CanRollbackGroup(group)) { Snackbar.Add(L["History:Actions:RollbackBlockedMetadata"], Severity.Warning); return; }
        var rows = await GetGroupRowsCoreAsync(group, owner);
        if (rows is null || owner.IsRetired) return;
        if (!rows.All(CanRollbackRow)) { Snackbar.Add(L["History:Actions:RollbackBlockedMetadata"], Severity.Warning); return; }
        var parameters = new DialogParameters<RollbackConfirmationDialog>();
        parameters.Add(dialog => dialog.Group, group);
        parameters.Add(dialog => dialog.GroupRows, rows);
        var result = await owner.ShowDialogAsync(() => DialogService.ShowAsync<RollbackConfirmationDialog>(L["Rollback:GroupTitle"], parameters, EditorOptions));
        await HandleRollbackResultAsync(result, owner);
    });

    private Task ShowHistoryAsync(ConfigurationHistoryDisplayRow history) => RunDialogAsync(async (owner, token) =>
    {
        var parameters = new DialogParameters<ConfigurationHistoryDiffDialog>();
        parameters.Add(dialog => dialog.Rows, history.Rows.ToArray());
        parameters.Add(dialog => dialog.Definitions, _definitions.ToArray());
        parameters.Add(dialog => dialog.FocusPath, history.DisplayPath);
        parameters.Add(dialog => dialog.Title, L["History:Dialog:MutationTitle", DefinitionDisplay(history.Representative.DefinitionKey)]);
        var result = await owner.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationHistoryDiffDialog>(L["History:Dialog:Title"], parameters, EditorOptions));
        await HandleRollbackResultAsync(result, owner);
    });

    private Task ShowGroupHistoryAsync(ConfigurationMutationGroup group) => RunDialogAsync(async (owner, token) =>
    {
        var definitions = _definitions.ToArray();
        var rows = await GetGroupRowsCoreAsync(group, owner);
        if (rows is null || owner.IsRetired) return;
        var parameters = new DialogParameters<ConfigurationHistoryDiffDialog>();
        parameters.Add(dialog => dialog.Group, group);
        parameters.Add(dialog => dialog.Rows, rows);
        parameters.Add(dialog => dialog.Definitions, definitions);
        parameters.Add(dialog => dialog.Title, L["History:Dialog:GroupTitle", group.Label]);
        var result = await owner.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationHistoryDiffDialog>(L["History:Dialog:GroupTitle", group.Label], parameters, EditorOptions));
        await HandleRollbackResultAsync(result, owner);
    });

    private static DialogOptions EditorOptions => new()
    {
        MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseOnEscapeKey = true, CloseButton = true
    };

    private async Task<IReadOnlyList<ConfigurationValueHistory>?> GetGroupRowsCoreAsync(ConfigurationMutationGroup group, ConfigurationOperationLifetime owner)
    {
        var result = await Facade.GetGroupHistoryAsync(group.GroupId);
        if (_disposed || owner.IsRetired) return null;
        if (result.IsFailed(out var error, out var rows)) { Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {error.Message}", Severity.Error); return null; }
        if (rows.Count == 0) { Snackbar.Add(L["History:Dialog:NoPreview"], Severity.Info); return null; }
        return rows.ToArray();
    }

    private async Task HandleRollbackResultAsync(DialogResult? result, ConfigurationOperationLifetime owner)
    {
        if (_disposed || owner.IsRetired || result is not { Canceled: false, Data: ConfigurationHistoryRollbackDialogResult rollback }) return;
        var summary = L[rollback.PostCommitIssues.Count == 0 ? "Rollback:Success" : "Rollback:AppliedWithIssues"].Value;
        Snackbar.Add(summary, rollback.PostCommitIssues.Count == 0 ? Severity.Success : Severity.Warning);
        if (rollback.PostCommitIssues.Count > 0)
        {
            var parameters = new DialogParameters<ConfigurationPostCommitIssuesDialog>();
            parameters.Add(dialog => dialog.Issues, rollback.PostCommitIssues);
            parameters.Add(dialog => dialog.SummaryText, summary);
            await owner.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationPostCommitIssuesDialog>(
                L["Dialogs:PostCommitIssues:Title"], parameters, ConfigurationPostCommitIssuesDialog.DefaultOptions));
        }
        if (!_disposed && !owner.IsRetired) await LoadAsync();
    }

    private string DefinitionDisplay(string definitionKey)
    {
        return _definitions.FirstOrDefault(definition =>
            string.Equals(definition.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? definitionKey;
    }

    private void NavigateToDefinition(string definitionKey, LogicalPath? path)
    {
        NavigationManager.NavigateTo(ConfigurationUiRoutes.State(definitionKey, path));
    }

    private async Task EnsureDefinitionDetailsCoreAsync(IEnumerable<string> definitionKeys,
        ConfigurationOperationLifetime owner, ConfigurationHistoryQuerySession.Query query)
    {
        var keys = definitionKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var definitionKey in keys)
        {
            if (!CanAdopt(owner, query)) return;
            if (_definitionDetails.ContainsKey(definitionKey) || !CanManageDefinition(definitionKey)) continue;
            var result = await Facade.GetDefinitionAsync(definitionKey);
            if (!CanAdopt(owner, query)) return;
            if (!result.IsFailed(out _, out var detail)) _queries.TryApply(query, () => _definitionDetails[definitionKey] = detail.Definition);
        }
    }

    private IReadOnlyList<string> DefinitionMetadataLines(string definitionKey)
    {
        var lines = new List<string>();
        var definition = ResolveDefinition(definitionKey);
        var summary = ResolveDefinitionSummary(definitionKey);
        lines.Add(L["History:DefinitionTooltip:DisplayName", definition?.DisplayName ?? summary?.DisplayName ?? definitionKey]);
        lines.Add(L["History:DefinitionTooltip:DefinitionKey", definitionKey]);

        var description = definition?.Description ?? summary?.Description;
        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add(L["History:DefinitionTooltip:Description", description]);
        }

        var sectionPath = definition?.SectionPath ?? summary?.SectionPath;
        if (!string.IsNullOrWhiteSpace(sectionPath))
        {
            lines.Add(L["History:DefinitionTooltip:SectionPath", sectionPath]);
        }

        var clrTypeName = definition?.ClrTypeName ?? summary?.ClrTypeName;
        if (!string.IsNullOrWhiteSpace(clrTypeName))
        {
            lines.Add(L["History:DefinitionTooltip:ClrType", clrTypeName]);
        }

        var project = definition?.FromProject ?? summary?.FromProject;
        if (!string.IsNullOrWhiteSpace(project))
        {
            lines.Add(L["History:DefinitionTooltip:Project", project]);
        }

        return lines;
    }

    private IReadOnlyList<string> PathMetadataLines(ConfigurationHistoryDisplayRow row)
    {
        var representative = row.Representative;
        var definition = ResolveDefinition(representative.DefinitionKey);
        var node = definition is null ? null : ResolveNodeByPath(definition.Root, row.DisplayPath);
        var configurationPath = node?.ConfigurationPath
                                ?? representative.SourceConfigurationPath
                                ?? representative.ConfigurationPath;

        var lines = new List<string>
        {
            L["History:PathTooltip:LogicalPath", row.DisplayPath.ToCanonicalString()]
        };

        if (!string.IsNullOrWhiteSpace(configurationPath))
        {
            lines.Add(L["History:PathTooltip:ConfigurationPath", configurationPath]);
        }

        if (node is not null)
        {
            lines.Add(L["History:PathTooltip:Node", NodeDisplay(node)]);
            lines.Add(L["History:PathTooltip:ClrType", node.ClrTypeName]);
            lines.Add(L["History:PathTooltip:NodeKind", node.NodeKind]);
            if (definition is not null)
            {
                lines.Add(L[
                    "History:PathTooltip:ReloadBehavior",
                    ConfigurationReloadBehaviorFormatter.Label(EffectiveReloadBehavior(definition, node), L)]);
            }

            if (!string.IsNullOrWhiteSpace(node.Description))
            {
                lines.Add(L["History:PathTooltip:Description", node.Description]);
            }
        }
        else if (definition is not null)
        {
            lines.Add(L["History:PathTooltip:DefinitionKey", definition.DefinitionKey]);
            lines.Add(L["History:PathTooltip:ClrType", definition.ClrTypeName]);
        }

        if (row.IsCollapsed)
        {
            lines.Add(L["History:PathTooltip:CollapsedRows", row.Rows.Count]);
        }

        return lines;
    }

    private ConfigurationDefinition? ResolveDefinition(string definitionKey)
    {
        return _definitionDetails.GetValueOrDefault(definitionKey);
    }

    private ConfigurationDefinitionSummary? ResolveDefinitionSummary(string definitionKey)
    {
        return _definitions.FirstOrDefault(definition =>
            string.Equals(definition.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanManageDefinition(string definitionKey)
    {
        return ResolveDefinitionSummary(definitionKey)?.CanManage is true;
    }

    private bool CanMutateDefinition(string definitionKey)
    {
        return ResolveDefinitionSummary(definitionKey)?.CanMutate is true;
    }

    private bool CanRollbackHistory(ConfigurationHistoryDisplayRow history)
    {
        return history.Rows.All(CanRollbackRow);
    }

    private bool CanRollbackGroup(ConfigurationMutationGroup group)
    {
        return group.Status != ConfigurationMutationGroupStatus.RolledBack
               && group.DefinitionKeys.All(CanMutateDefinition);
    }

    private bool CanRollbackRow(ConfigurationValueHistory row)
    {
        var definition = ResolveDefinitionSummary(row.DefinitionKey);
        return definition is { CanMutate: true }
               && definition.SchemaVersion == row.SchemaVersion
               && !string.IsNullOrWhiteSpace(row.SchemaHash)
               && string.Equals(definition.SchemaHash, row.SchemaHash, StringComparison.Ordinal);
    }

    private string RollbackTooltip(bool canRollback)
    {
        return canRollback
            ? L["Rollback:Confirm"].Value
            : L["History:Actions:RollbackBlockedMetadata"].Value;
    }

    private string DefinitionFilterLabel(ConfigurationDefinitionSummary definition)
    {
        if (!definition.CanManage)
        {
            return L["History:Filters:UnavailableDefinition", definition.DisplayName];
        }

        return definition.LifecycleState == ConfigurationDefinitionLifecycleState.Retired
            ? L["History:Filters:RetiredDefinition", definition.DisplayName]
            : definition.DisplayName;
    }

    private static ConfigurationNodeDefinition? ResolveNodeByPath(ConfigurationNodeDefinition root, LogicalPath path)
    {
        ConfigurationNodeDefinition? current = root;
        foreach (var segment in path.Segments)
        {
            current = ResolveChildNode(current, segment);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static ConfigurationNodeDefinition? ResolveChildNode(ConfigurationNodeDefinition current, ConfigurationPathSegment segment)
    {
        return segment switch
        {
            PropertySegment property => current.Children.FirstOrDefault(child =>
                string.Equals(child.Name, property.Name, StringComparison.OrdinalIgnoreCase)),
            DictionaryKeySegment => current.DictionaryTemplate?.ValueTemplate,
            ListItemKeySegment or ListIndexSegment => current.ListTemplate?.ItemTemplate,
            _ => null
        };
    }

    private string NodeDisplay(ConfigurationNodeDefinition node)
    {
        return string.IsNullOrWhiteSpace(node.DisplayName) ? node.Name : node.DisplayName!;
    }

    private static ConfigurationReloadBehavior EffectiveReloadBehavior(
        ConfigurationDefinition definition,
        ConfigurationNodeDefinition node)
    {
        return node.ResolveEffectiveReloadBehavior(definition);
    }

    private string GroupLabel(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return string.Empty;
        }

        return _groups.FirstOrDefault(group => group.GroupId == groupId)?.Label ?? groupId;
    }

    private string StatusText(ConfigurationMutationGroupStatus status)
    {
        return L[$"Group:StatusValues:{status}"];
    }

    private string TargetFilterText(string? value)
    {
        return value switch
        {
            TARGET_FILTER_MONICA => L["History:Filters:TargetMonica"],
            TARGET_FILTER_EXTERNAL => L["History:Filters:TargetExternal"],
            _ => L["History:Filters:TargetAll"]
        };
    }

    private string HistoryTargetLabel(IReadOnlyList<ConfigurationValueHistory> rows)
    {
        if (rows.All(row => row.TargetKind == ConfigurationMutationTargetKind.MonicaEffectiveStore))
        {
            return L["History:Targets:MonicaShort"];
        }

        return rows.All(row => row.TargetKind == ConfigurationMutationTargetKind.ExternalConfigurationSource)
            ? L["History:Targets:ExternalShort"]
            : L["History:Targets:Mixed"];
    }

    private string HistoryTargetTooltip(IReadOnlyList<ConfigurationValueHistory> rows)
    {
        if (rows.Count != 1)
        {
            return HistoryTargetLabel(rows);
        }

        var history = rows[0];
        if (history.TargetKind != ConfigurationMutationTargetKind.ExternalConfigurationSource)
        {
            return L["History:Targets:Monica"];
        }

        var sourceName = history.SourceDisplayName ?? L["History:Targets:External"];
        var sourcePath = history.SourcePhysicalPath ?? L["Common:States:Empty"];
        var configurationPath = history.SourceConfigurationPath ?? history.ConfigurationPath ?? L["Common:States:Empty"];
        return L["History:Targets:ExternalTooltip", sourceName, configurationPath, sourcePath];
    }

    private static string HistoryTargetIcon(IReadOnlyList<ConfigurationValueHistory> rows)
    {
        return rows.Any(row => row.TargetKind == ConfigurationMutationTargetKind.ExternalConfigurationSource)
            ? MoIcons.FileText
            : MoIcons.Database;
    }
}
