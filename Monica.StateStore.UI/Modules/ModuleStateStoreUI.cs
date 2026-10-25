using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.StateStore.UI.Pages;
using Monica.StateStore.UI.Localization;
using Monica.StateStore.UI.Services;
using Monica.StateStore.UI.Services.Browser;
using Monica.UI.Shell.Models;
using Monica.UI.Localization;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleStateStoreUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the StateStoreUI module
        /// </summary>
        public ModuleRegistration<ModuleStateStoreUI, ModuleStateStoreUIOption> AddStateStoreUI(
            Action<ModuleStateStoreUIOption>? action = null)
        {
            return builder.AddModule<ModuleStateStoreUI, ModuleStateStoreUIOption>(action);
        }
    }
}

/// <summary>
/// StateStore UI module - provides state storage management interface
/// </summary>
public class ModuleStateStoreUI : MonicaModule<ModuleStateStoreUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleStateStore, ModuleStateStoreOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(static option =>
        {
            option.AddResource<StateStoreResource>();
            option.AddResource<ModuleSystemResource>();
        });
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<StateStoreResource>(
                    "Monica.StateStore", "Navigation:Category", 20,
                    parentId: BuiltInNavigationCategoryIds.Debug,
                    icon: MoIcons.Database);

                registry.RegisterLocalizedPage<UIStateStoreDashboardPage, StateStoreResource>(
                    UIStateStoreDashboardPage.PAGE_URL,
                    "Pages:StateStoreManage:Title",
                    MoIcons.Database,
                    category,
                    addToNav: true,
                    navOrder: 20);
                registry.RegisterLocalizedPage<UIStateStoreProvidersPage, StateStoreResource>(
                    UIStateStoreProvidersPage.PAGE_URL,
                    "Workbench:Providers",
                    MoIcons.Database,
                    category,
                    addToNav: true,
                    navOrder: 10);
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleStateStoreUIOption> context)
    {
        var services = context.Services;
        services.AddScoped<StateStoreUIService>();
        services.AddSingleton<IStateStoreBrowserApi, RedisStateStoreBrowserApi>();
        services.AddSingleton<IStateStoreBrowserApi, MemoryStateStoreBrowserApi>();
        services.AddSingleton<IStateStoreBrowserApi, DaprStateStoreBrowserApi>();
        services.AddSingleton<IStateStoreBrowserApi, FallbackStateStoreBrowserApi>();
    }
}

/// <summary>
/// StateStore UI module options
/// </summary>
public class ModuleStateStoreUIOption : ModuleOptions<ModuleStateStoreUI>
{
    /// <summary>
    /// Default Key scan mode
    /// </summary>
    public string DefaultScanPattern { get; set; } = "*";

    /// <summary>
    /// Maximum number of keys per page
    /// </summary>
    public int MaxKeysPerPage { get; set; } = 50;

    /// <summary>
    /// Allow editing of Key (set to false for read-only mode)
    /// </summary>
    public bool AllowKeyEditing { get; set; } = true;

    /// <summary>
    /// Allow deletion of Key
    /// </summary>
    public bool AllowKeyDeletion { get; set; } = true;

    /// <summary>
    /// Allow creation of Key
    /// </summary>
    public bool AllowKeyCreation { get; set; } = true;
}
