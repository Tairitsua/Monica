using Monica.UI.Icons;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.UI.Localization;
using Monica.UI.Pages;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.UIModuleSystem.State;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleSystemUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the module system dashboard UI module.
        /// </summary>
        public ModuleRegistration<ModuleSystemUI, ModuleSystemUIOption> AddModuleSystemUI(
            Action<ModuleSystemUIOption>? action = null)
        {
            return builder.AddModule<ModuleSystemUI, ModuleSystemUIOption>(action);
        }
    }
}

/// <summary>
/// Module system dashboard UI module.
/// </summary>
public class ModuleSystemUI : MonicaModule<ModuleSystemUIOption>, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleSystem, ModuleSystemOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<ModuleSystemResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(RegisterNavigation));
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleSystemUIOption> context)
    {
        context.Services.AddSingleton<ModuleSystemWorkbenchSessionFactory>();
        context.Services.TryAddScoped<OperationalPageAccessPolicy<ModuleSystemUIOption>>();
    }

    private static void RegisterNavigation(INavigationRegistryBuilder registry)
    {
        registry.RegisterLocalizedPage<ModuleSystemPage, ModuleSystemResource>(
            ModuleSystemPage.MODULE_SYSTEM_DASHBOARD_URL,
            "Sections:Overview",
            MoIcons.Dashboard,
            BuiltInNavigationCategoryIds.Module,
            addToNav: true,
            navOrder: 10,
            accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleSystemUIOption>),
            descriptionKey: "Navigation:Descriptions:Overview");
        RegisterSection("overview", "Sections:Overview", "Navigation:Descriptions:Overview", MoIcons.Dashboard, 10, addToNav: false);
        RegisterSection("performance", "Sections:Performance", "Navigation:Descriptions:Performance", MoIcons.ChartLine, 20);
        RegisterSection("modules", "Sections:Modules", "Navigation:Descriptions:Modules", MoIcons.Grid, 30);
        RegisterSection("dependencies", "Sections:Dependencies", "Navigation:Descriptions:Dependencies", MoIcons.Workflow, 40);
        RegisterSection("discovery", "Sections:Discovery", "Navigation:Descriptions:Discovery", MoIcons.FileSearch, 50);

        void RegisterSection(string segment, string labelKey, string descriptionKey, string icon, int order, bool addToNav = true)
        {
            registry.RegisterLocalizedPage<ModuleSystemPage, ModuleSystemResource>(
                $"{ModuleSystemPage.MODULE_SYSTEM_DASHBOARD_URL}/{segment}",
                labelKey,
                icon,
                BuiltInNavigationCategoryIds.Module,
                addToNav,
                order,
                accessPolicyType: typeof(OperationalPageAccessPolicy<ModuleSystemUIOption>),
                descriptionKey: descriptionKey);
        }
    }
}

/// <summary>
/// Options for the module system dashboard UI module.
/// </summary>
public class ModuleSystemUIOption : ModuleOptions<ModuleSystemUI>, IOperationalPageAccessOptions
{
    /// <summary>
    /// Gets or sets the host authorization policy that overrides the shell-wide operational policy for this page.
    /// The default is <see langword="null"/>. A missing or blank value inherits
    /// <see cref="OperationalPageAccessOption.AuthorizationPolicy"/>. Configure an override only when this page needs
    /// a different policy; Development access and operational Debug mode ignore it.
    /// </summary>
    public string? AuthorizationPolicyOverride { get; set; }
}
