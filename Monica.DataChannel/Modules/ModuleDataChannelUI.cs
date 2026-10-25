using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.DataChannel.Localization;
using Monica.DataChannel.Pages;
using Monica.DataChannel.UIDataChannel.State;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// UI module for DataChannel.
/// Provides the management interface for DataChannel.
/// </summary>
public class ModuleDataChannelUI : MonicaModule<ModuleDataChannelUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDataChannel, ModuleDataChannelOption>();
        module.Require<ModuleStackTraceUI, ModuleStackTraceUIOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<DataChannelResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<DataChannelResource>(
                    "Monica.DataChannel", "Navigation:Category", 80,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Route);

                registry.RegisterLocalizedPage<UIDataChannelPage, DataChannelResource>(
                    UIDataChannelPage.PAGE_URL,
                    "Pages:DataChannelManage:Title",
                    MoIcons.Route,
                    category,
                    addToNav: true,
                    navOrder: 30,
                    descriptionKey: "Workbench:DirectoryDescription");
                registry.RegisterLocalizedPage<UIDataChannelExceptionsPage, DataChannelResource>(
                    UIDataChannelExceptionsPage.PAGE_URL,
                    "Pages:DataChannelExceptions:Title",
                    MoIcons.Warning,
                    category,
                    addToNav: true,
                    navOrder: 31,
                    descriptionKey: "Workbench:ExceptionsDescription");
            }));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleDataChannelUIOption> context)
    {
        context.Services.AddScoped<DataChannelWorkbenchState>();
    }
}

public static class ModuleDataChannelUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the DataChannelUI module.
        /// </summary>
        public ModuleRegistration<ModuleDataChannelUI, ModuleDataChannelUIOption> AddDataChannelUI(
            Action<ModuleDataChannelUIOption>? action = null)
        {
            return builder.AddModule<ModuleDataChannelUI, ModuleDataChannelUIOption>(action);
        }
    }
}

/// <summary>
/// Options for the DataChannel UI module.
/// </summary>
public class ModuleDataChannelUIOption : ModuleOptions<ModuleDataChannelUI>
{
}
