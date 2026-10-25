using Monica.Configuration.Models;

namespace Monica.Configuration.UI.Pages;

public partial class ConfigurationHistoryPage
{
    private int _selectedTab;

    private void NavigateToGroupDefinition(string definitionKey) => NavigateToDefinition(definitionKey, null);

    private void SelectTarget(string target) => _targetFilter = target;

    private string TargetButtonClass(string target) =>
        $"configuration-history-target configuration-history-target--{target}{(_targetFilter == target ? " is-selected" : string.Empty)}";

    private static string MutationClass(ConfigurationMutationKind kind) =>
        kind == ConfigurationMutationKind.Remove ? "configuration-history-kind--remove" : "configuration-history-kind--set";

    private static string HistoryTargetClass(IReadOnlyList<ConfigurationValueHistory> rows) =>
        rows.Any(row => row.TargetKind == ConfigurationMutationTargetKind.ExternalConfigurationSource)
            ? "configuration-history-badge configuration-history-badge--external"
            : "configuration-history-badge configuration-history-badge--monica";

    private static string GroupStatusClass(ConfigurationMutationGroupStatus status) => status switch
    {
        ConfigurationMutationGroupStatus.Applied => "configuration-history-badge configuration-history-badge--applied",
        ConfigurationMutationGroupStatus.PartiallyApplied => "configuration-history-badge configuration-history-badge--partial",
        _ => "configuration-history-badge configuration-history-badge--rollback"
    };

    private string DisplayPath(LogicalPath path) => path.Depth == 0 ? L["History:Workbench:Root"].Value : path.ToCanonicalString();

    private string AuditOperator(string? name, string? id) => !string.IsNullOrWhiteSpace(name)
        ? name : !string.IsNullOrWhiteSpace(id) ? id : L["History:Workbench:UnknownOperator"].Value;

    private string HistoryQuerySummary => _loadedHistoryRequest is null
        ? L["History:Workbench:QueryPending"].Value
        : string.Join(" · ", ScopeParts(_loadedHistoryRequest.DefinitionKey, _loadedHistoryRequest.From, _loadedHistoryRequest.To)
            .Concat([TargetFilterText(_loadedHistoryRequest.TargetKind switch
            {
                ConfigurationMutationTargetKind.MonicaEffectiveStore => TARGET_FILTER_MONICA,
                ConfigurationMutationTargetKind.ExternalConfigurationSource => TARGET_FILTER_EXTERNAL,
                _ => TARGET_FILTER_ALL
            })])
            .Concat(_loadedHistoryRequest.LogicalPath is null ? [] : new[] { DisplayPath(_loadedHistoryRequest.LogicalPath) }));

    private string GroupQuerySummary => _loadedGroupRequest is null
        ? L["History:Workbench:QueryPending"].Value
        : string.Join(" · ", ScopeParts(_loadedGroupRequest.DefinitionKey, _loadedGroupRequest.From, _loadedGroupRequest.To));

    private IEnumerable<string> ScopeParts(string? definitionKey, DateTimeOffset? from, DateTimeOffset? to)
    {
        yield return string.IsNullOrWhiteSpace(definitionKey) ? L["History:Workbench:AllDefinitions"].Value : DefinitionDisplay(definitionKey);
        yield return from is null && to is null ? L["History:Workbench:AllDates"].Value
            : L["History:Workbench:DateRange", from is { } start ? TimeDisplay.FormatDate(start) : L["History:Workbench:OpenStart"].Value,
                to is { } end ? TimeDisplay.FormatDate(end) : L["History:Workbench:OpenEnd"].Value].Value;
    }
}
