using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.UI.Shell.Models;
using Monica.Utilities.Localization;
using Monica.Utilities.Pages;
using Monica.Utilities.UIUtilities.State;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Registers the connectivity and text transformation workspaces inside the Monica shell.
/// </summary>
public class ModuleUtilitiesUI : MonicaModule<ModuleUtilitiesUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleUtilities, ModuleUtilitiesOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<UtilitiesResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<UtilitiesResource>(
                    "Monica.Utilities", "Navigation:Category", 70,
                    parentId: BuiltInNavigationCategoryIds.Infrastructure,
                    icon: MoIcons.Wrench);

                registry.RegisterLocalizedPage<UIUtilitiesPage, UtilitiesResource>(
                    UIUtilitiesPage.PAGE_URL,
                    "Pages:UtilitiesToolbox:Title",
                    MoIcons.Network,
                    category,
                    addToNav: true,
                    navOrder: 38);
                registry.RegisterLocalizedPage<UIUtilitiesTextPage, UtilitiesResource>(
                    UIUtilitiesTextPage.PAGE_URL,
                    "Pages:TextTransform:Title",
                    MoIcons.Code,
                    category,
                    addToNav: true,
                    navOrder: 39);
            }));
    }

    /// <summary>
    /// Registers circuit-scoped draft and accepted-result state borrowed by the tool panels.
    /// Panels retain their drafts when navigating between sibling routes and cancel their own
    /// pending operations when retiring; injected state and facades remain owned by the circuit.
    /// </summary>
    public override void ConfigureServices(ModuleContext<ModuleUtilitiesUIOption> context)
    {
        context.Services.AddScoped<ConnectivityProbeState>();
        context.Services.AddScoped<TextTransformState>();
    }
}

/// <summary>
/// Builder extensions for the utilities UI module.
/// </summary>
public static class ModuleUtilitiesUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the utilities toolbox UI module.
        /// </summary>
        /// <param name="action">Optional configuration applied to <see cref="ModuleUtilitiesUIOption" />.</param>
        /// <returns>The host-bound utilities UI registration.</returns>
        public ModuleRegistration<ModuleUtilitiesUI, ModuleUtilitiesUIOption> AddUtilitiesUI(
            Action<ModuleUtilitiesUIOption>? action = null)
        {
            return builder.AddModule<ModuleUtilitiesUI, ModuleUtilitiesUIOption>(action);
        }
    }
}

/// <summary>
/// Options for the utilities toolbox UI module.
/// </summary>
public class ModuleUtilitiesUIOption : ModuleOptions<ModuleUtilitiesUI>
{
}
