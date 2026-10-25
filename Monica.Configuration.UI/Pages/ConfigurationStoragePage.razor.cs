using Monica.Configuration.Models;
using Monica.Configuration.UI.State;
using Monica.Configuration.UI.Support;

namespace Monica.Configuration.UI.Pages;

/// <summary>Reviews registered storage capabilities and independently accepted store-operation evidence.</summary>
/// <remarks>
/// Observations are local accepted reads, not a single transaction or continuous health check. The page owns
/// tokenless storage reads. Custom owners must await teardown; services remain borrowed.
/// </remarks>
public partial class ConfigurationStoragePage
{
    /// <summary>Gets the Configuration Storage route.</summary>
    public const string PAGE_URL = ConfigurationUiRoutes.STORAGE_ROUTE;
    private readonly ConfigurationOperationLifetime _operations = new();
    private ConfigurationStoragePageState? _state;
    private ConfigurationStorageOverview? _overview => _state?.Overview.Value;
    private IReadOnlyList<ConfigurationStoreState> _states => _state?.StoreStates.Value ?? [];
    private bool _loading => _state?.Loading ?? true;
    private bool _hasLoadedOverview => _state?.Overview.HasLoaded ?? false;
    private bool _hasLoadedStoreStates => _state?.StoreStates.HasLoaded ?? false;
    private string? _overviewLoadError => _state?.Overview.Error;
    private string? _storeStateLoadError => _state?.StoreStates.Error;
    private DateTimeOffset? _overviewObservedAtUtc => _state?.Overview.ObservedAtUtc;
    private DateTimeOffset? _storeStatesObservedAtUtc => _state?.StoreStates.ObservedAtUtc;

    private IReadOnlyList<StoreDossier> StoreDossiers => _overview is not { } overview ? []
        : new[]
        {
            (Label: L["Storage:Roles:EffectiveValues"].Value, Store: overview.EffectiveValueStore),
            (Label: L["Storage:Roles:History"].Value, Store: overview.HistoryStore),
            (Label: L["Storage:Roles:Metadata"].Value, Store: overview.MetadataStore)
        }
        .GroupBy(static role => role.Store.StoreKey, StringComparer.OrdinalIgnoreCase)
        .Select(static group => new StoreDossier(group.First().Store, group.Select(static role => role.Label).ToArray()))
        .ToArray();

    protected override async Task OnInitializedAsync()
    {
        _state = new(Facade, L["Common:Errors:LoadFailed"], _operations);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_operations.IsRetired || _state is null) return;
        try { await _state.LoadAsync(); }
        catch (OperationCanceledException) when (_operations.IsRetired) { }
    }

    private static bool HasRecordedOperation(ConfigurationStoreState? state) =>
        state is not null && (state.LastOperationTime.HasValue ||
                              !state.LastOperationSucceeded ||
                              !string.IsNullOrWhiteSpace(state.LastError));

    /// <summary>Joins admitted storage reads before releasing the page.</summary>
    public ValueTask DisposeAsync() => _operations.RetireAsync();

    private sealed record StoreDossier(ConfigurationStoreDescriptor Store, IReadOnlyList<string> Roles);
}
