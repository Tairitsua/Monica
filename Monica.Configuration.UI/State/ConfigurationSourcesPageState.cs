using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.UI.Support;
using Monica.Core.Results;

namespace Monica.Configuration.UI.State;

/// <summary>Owns the runtime provider inventory accepted by one sources page.</summary>
/// <remarks>
/// Refresh retains accepted evidence until its newest outcome. Provider collections are detached on acceptance.
/// Services are borrowed; await disposal to drain actual tokenless reads without adopting late results.
/// </remarks>
internal sealed class ConfigurationSourcesPageState : IAsyncDisposable
{
    private readonly ConfigurationOperationLifetime _lifetime;
    private readonly Func<Task<Res<IReadOnlyList<ConfigurationSourceInventory>>>> _inventories;

    internal ConfigurationSourcesPageState(ConfigurationFacade facade, string unavailableText,
        ConfigurationOperationLifetime lifetime, bool showSensitiveValues = false)
        : this(() => facade.GetConfigurationSourceInventoriesAsync(showSensitiveValues), unavailableText, lifetime: lifetime) { }

    internal ConfigurationSourcesPageState(Func<Task<Res<IReadOnlyList<ConfigurationSourceInventory>>>> inventories,
        string unavailableText, TimeProvider? timeProvider = null, ConfigurationOperationLifetime? lifetime = null)
    {
        _lifetime = lifetime ?? new();
        _inventories = inventories;
        SourceInventories = new(_lifetime, unavailableText, timeProvider);
    }

    internal ConfigurationReadState<IReadOnlyList<ConfigurationSourceInventory>> SourceInventories { get; }
    internal bool Loading => SourceInventories.Loading;
    internal bool HasAcceptedEvidence => SourceInventories.HasLoaded;

    internal Task LoadAsync() => _lifetime.RunAsync(_ =>
        SourceInventories.ReadReservedAsync(SourceInventories.BeginRead(), _inventories,
            values => Array.AsReadOnly(values.Select(inventory => inventory with
            {
                Items = Array.AsReadOnly(inventory.Items.ToArray())
            }).ToArray())));

    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
}
