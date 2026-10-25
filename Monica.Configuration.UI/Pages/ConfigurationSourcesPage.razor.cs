using Monica.Configuration.Models;
using Monica.Configuration.UI.Dialogs;
using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;
using MudBlazor;

namespace Monica.Configuration.UI.Pages;

/// <summary>Reviews runtime provider contributions and their effective or overridden values.</summary>
/// <remarks>The page owns inventory reads and file dialogs. Await teardown to drain admitted work; services remain borrowed.</remarks>
public partial class ConfigurationSourcesPage
{
    /// <summary>Gets the runtime configuration sources route.</summary>
    public const string PAGE_URL = ConfigurationUiRoutes.SOURCES_ROUTE;
    private readonly ConfigurationOperationLifetime _operations = new();
    private ConfigurationSourcesPageState? _state;
    private bool _fileDialogOpen;
    private IReadOnlyList<ConfigurationSourceInventory> _sourceInventories => _state?.SourceInventories.Value ?? [];
    private bool _loading => _state?.Loading ?? true;
    private bool _hasLoadedSourceInventories => _state?.SourceInventories.HasLoaded ?? false;
    private string? _sourceInventoryLoadError => _state?.SourceInventories.Error;
    private DateTimeOffset? _sourceInventoriesObservedAtUtc => _state?.SourceInventories.ObservedAtUtc;

    protected override async Task OnInitializedAsync()
    {
        _state = new(Facade, L["Common:Errors:LoadFailed"], _operations, Visibility.ShowSensitiveValues);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_operations.IsRetired || _state is null) return;
        try { await _state.LoadAsync(); }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
    }

    private async Task ViewFileAsync(ConfigurationSourceDescriptor source)
    {
        var sourceKey = source.SourceKey;
        var title = L["Sources:File:Title", source.DisplayName].Value;
        var admitted = false;
        if (!_operations.TryApply(() =>
            {
                if (_fileDialogOpen) return;
                _fileDialogOpen = admitted = true;
            }) || !admitted) return;
        try
        {
            var parameters = new DialogParameters<ConfigurationSourceFileDialog>();
            parameters.Add(dialog => dialog.SourceKey, sourceKey);
            await _operations.ShowDialogAsync(() => DialogService.ShowAsync<ConfigurationSourceFileDialog>(title, parameters,
                new DialogOptions { MaxWidth = MaxWidth.ExtraLarge, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true }));
        }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
        catch (Exception exception) when (!_operations.IsRetired)
        {
            Snackbar.Add($"{L["Common:Errors:LoadFailed"]}: {exception.Message}", Severity.Error);
        }
        finally { _operations.TryApply(() => _fileDialogOpen = false); }
    }

    /// <summary>Closes owned file dialogs and joins actual reads and native results before releasing the page.</summary>
    public ValueTask DisposeAsync() => _operations.RetireAsync();

    private string SourcePath(ConfigurationSourceDescriptor source)
    {
        return source.PhysicalPath ?? source.SourcePath ?? source.ProviderType;
    }

    private string ReloadText(ConfigurationSourceDescriptor source)
    {
        return source.ReloadOnChange is null
            ? L["Sources:Reload:Unknown"]
            : source.ReloadOnChange.Value ? L["Sources:Reload:Enabled"] : L["Sources:Reload:Disabled"];
    }

    private IReadOnlyList<SourceInventoryGroup> SourceInventoryGroups(ConfigurationSourceInventory inventory)
    {
        return inventory.Items
            .GroupBy(SourceInventoryGroupKey)
            .Select(group =>
            {
                var items = group
                    .OrderBy(static item => item.RelativeConfigurationPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new SourceInventoryGroup(
                    GroupTitle(items[0]),
                    GroupSubtitle(items[0]),
                    items,
                    items.Count(static item => item.IsEffective),
                    items.Count(static item => !item.IsManagedByMonica));
            })
            .OrderBy(group => group.UnmanagedCount > 0 ? 1 : 0)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string SourceInventoryGroupKey(ConfigurationSourceInventoryItem item)
    {
        return item.IsManagedByMonica
            ? item.DefinitionKey ?? item.DefinitionDisplayName ?? item.ConfigurationPath
            : L["Sources:Inventory:UnmanagedDefinition"].Value;
    }

    private string GroupTitle(ConfigurationSourceInventoryItem item)
    {
        return item.IsManagedByMonica
            ? item.DefinitionDisplayName ?? L["Sources:Inventory:UnknownDefinition"]
            : L["Sources:Inventory:UnmanagedDefinition"];
    }

    private string GroupSubtitle(ConfigurationSourceInventoryItem item)
    {
        if (!item.IsManagedByMonica)
        {
            return L["Sources:Inventory:RuntimeKey"];
        }

        return item.DefinitionKey ?? string.Empty;
    }

    private string FieldLabel(ConfigurationSourceInventoryItem item)
    {
        return string.IsNullOrWhiteSpace(item.NodeLabel)
            ? item.RelativeConfigurationPath
            : item.NodeLabel!;
    }

    private string DisplayValue(ConfigurationSourceInventoryItem item)
    {
        return Visibility.ShouldMask(item.IsSensitive) ? L["State:Value:Sensitive"] : item.DisplayValue ?? L["Value:States:Empty"];
    }

    private ConfigurationSourceDescriptor? OverridingSourceFor(
        ConfigurationSourceInventoryItem item,
        ConfigurationSourceDescriptor currentSource)
    {
        if (item.IsEffective)
        {
            return null;
        }

        return _sourceInventories
            .OrderByDescending(static inventory => inventory.Source.PriorityIndex)
            .FirstOrDefault(inventory =>
                !string.Equals(inventory.Source.SourceKey, currentSource.SourceKey, StringComparison.Ordinal)
                && inventory.Items.Any(candidate => candidate.IsEffective && HasSameConfigurationPath(candidate, item)))
            ?.Source;
    }

    private string OverriddenTooltip(ConfigurationSourceDescriptor? source)
    {
        return source is null
            ? L["Sources:Inventory:OverriddenUnknownTooltip"]
            : L["Sources:Inventory:OverriddenTooltip", source.DisplayName, source.PriorityIndex, SourcePath(source)];
    }

    private static bool HasSameConfigurationPath(
        ConfigurationSourceInventoryItem left,
        ConfigurationSourceInventoryItem right)
    {
        return string.Equals(left.ConfigurationPath, right.ConfigurationPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SourceInventoryGroup(
        string Title,
        string Subtitle,
        IReadOnlyList<ConfigurationSourceInventoryItem> Items,
        int EffectiveCount,
        int UnmanagedCount);

    private string _sourceSearchText = string.Empty;
    private IEnumerable<ConfigurationSourceInventory> FilteredSourceInventories => string.IsNullOrWhiteSpace(_sourceSearchText)
        ? _sourceInventories : _sourceInventories.Where(inventory => SourceMatches(inventory.Source));

    private bool SourceMatches(ConfigurationSourceDescriptor source) =>
        new[] { source.DisplayName, source.SourceKey, source.PhysicalPath, source.SourcePath, source.ProviderType, source.Kind.ToString() }
            .Any(value => value?.Contains(_sourceSearchText, StringComparison.OrdinalIgnoreCase) == true);
}
