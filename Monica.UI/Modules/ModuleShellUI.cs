using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.UI.Localization;
using Monica.UI.Icons;
using Monica.UI.Pages;
using Monica.UI.Shell.Components;
using Monica.UI.Shared.Components.Markdown;
using Monica.UI.Shell.State;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
using Monica.UI.Theming;
using MudBlazor;
using MudBlazor.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleShellUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the shell UI module.
        /// </summary>
        public ModuleRegistration<ModuleShellUI, ModuleShellUIOption> AddUIShell(
            Action<ModuleShellUIOption>? action = null)
        {
            return builder.AddModule<ModuleShellUI, ModuleShellUIOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleShellUI, ModuleShellUIOption> registration)
    {
        /// <summary>
        /// Adds a route redirect emitted by the shell during endpoint mapping.
        /// </summary>
        /// <param name="fromPath">The source route.</param>
        /// <param name="toPath">The redirect target.</param>
        /// <returns>The same host-bound shell registration.</returns>
        public ModuleRegistration<ModuleShellUI, ModuleShellUIOption> AddRouteRedirect(
            string fromPath,
            string toPath)
        {
            registration.Require<ModuleUIHost, ModuleUIHostOption>().AddRouteRedirect(fromPath, toPath);
            return registration;
        }
    }
}

/// <summary>
/// Shell UI module.
/// Provides the shared Blazor shell infrastructure for Monica UI modules.
/// </summary>
public class ModuleShellUI : MonicaModule<ModuleShellUIOption>, IWebHostRequiredModule, IUIModule
{
    /// <summary>
    /// Declare module dependencies
    /// </summary>
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleUIHost, ModuleUIHostOption>(options =>
        {
            options.CatalogPageLayout = typeof(Monica.UI.Shell.Components.Layout.MainLayout);
            options.DefaultLayout = typeof(Monica.UI.Shell.Components.Layout.MainLayout);
        });
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            options => options.AddResource<NavigationSearchResource>());
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleShellUIOption> context)
    {
        context.Services.AddScoped<OperationalPageAccessEvaluator>();
        context.Services.AddScoped<NavigationSearchCatalog>();
        context.Services.AddScoped<ShellState>();
        context.Services.AddScoped<UserContextState>();
        if (Option.OperationalPageAccess.DebugMode)
        {
            Logger.LogWarning("OperationalPageAccess.DebugMode is enabled. Monica operational diagnostics bypass authorization in every environment.");
        }
    }

    /// <inheritdoc />
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleShellUIOption> context)
        => Option.ApplyNavigation(context.ApplicationBuilder.ApplicationServices.GetRequiredService<INavigationRegistryBuilder>());

    /// <inheritdoc />
    protected override ModuleWebStage GetApplicationBuilderStage() => ModuleWebStage.AfterRouting;

}

/// <summary>
/// Shell UI module options.
/// </summary>
public class ModuleShellUIOption : ModuleOptions<ModuleShellUI>
{
    private readonly List<Action<INavigationRegistryBuilder>> _navigationContributions = [];

    /// <summary>
    /// Application name displayed in the shell app bar.
    /// When not configured, the shell uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Application identifier displayed before the version badge.
    /// When not configured, the shell uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Application version displayed in the shell.
    /// When not configured, the shell uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>
    /// and finally <c>v1.0</c>.
    /// </summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Resolves the application name displayed in the shell app bar.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no explicit module value is configured and the option has not been bound to a Monica host.
    /// </exception>
    public string GetAppName() => MonicaApplicationOptions.Normalize(AppName)
                                  ?? Application.ResolveAppName(fallback: nameof(Monica));

    /// <summary>
    /// Resolves the application identifier displayed before the version badge.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no explicit module value is configured and the option has not been bound to a Monica host.
    /// </exception>
    public string GetAppId() => MonicaApplicationOptions.Normalize(AppId)
                                ?? Application.ResolveAppId();

    /// <summary>
    /// Resolves the application version displayed in the shell.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no explicit module value is configured and the option has not been bound to a Monica host.
    /// </exception>
    public string GetAppVersion() => MonicaApplicationOptions.Normalize(AppVersion)
                                     ?? Application.ResolveAppVersion(fallback: "v1.0")!;

    /// <summary>
    /// Gets the shared authorization configuration for Monica operational pages and protected diagnostics surfaces.
    /// </summary>
    /// <remarks>
    /// Development is accessible by default. Outside Development, pages use their optional policy override and then
    /// <see cref="OperationalPageAccessOption.AuthorizationPolicy"/>. Enabling
    /// <see cref="OperationalPageAccessOption.DebugMode"/> bypasses both policies in every environment.
    /// </remarks>
    public OperationalPageAccessOption OperationalPageAccess { get; } = new();

    /// <summary>
    /// The desktop navigation layout used when the browser has no saved shell preference.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ShellNavigationLayout.Sidebar"/>. Appearance settings persist a browser-specific
    /// override. Both layouts use a temporary navigation drawer on narrow screens.
    /// </remarks>
    public ShellNavigationLayout DefaultNavigationLayout { get; set; } = ShellNavigationLayout.Sidebar;

    /// <summary>
    /// The maximum number of categories displayed in the top navigation bar (the excess will be placed in the "More" menu)
    /// </summary>
    public int MaxVisibleCategories { get; set; } = 10;

    /// <summary>
    /// Whether to show navigation search and enable its Ctrl/Cmd+K shortcut. Defaults to true.
    /// When no <see cref="NavBarSearchAction"/> is configured, the shell searches authorized registered navigation pages.
    /// </summary>
    public bool EnableNavBarSearch { get; set; } = true;

    /// <summary>
    /// Gets or sets the host-owned action invoked by the navigation bar search control.
    /// </summary>
    /// <remarks>
    /// When null, the shell opens its built-in registered-page search palette. A configured callback replaces that
    /// palette for both the toolbar control and Ctrl/Cmd+K. The host owns its custom dialog, routing, and cancellation.
    /// Repeated requests share the callback's incomplete task. Shell retirement observes that task to completion;
    /// the host must finish it and remains responsible for retiring any resources it creates.
    /// </remarks>
    public Func<Task>? NavBarSearchAction { get; set; }

    /// <summary>
    /// Gets or sets the host-owned action invoked by the navigation bar notifications control.
    /// </summary>
    /// <remarks>
    /// The notifications control is rendered only when this delegate is configured.
    /// </remarks>
    public Func<Task>? NavBarNotificationsAction { get; set; }

    /// <summary>
    /// Gets or sets the host-owned action invoked by the profile menu item.
    /// </summary>
    public Func<Task>? UserProfileAction { get; set; }

    /// <summary>
    /// Gets or sets the host-owned action invoked by the user settings menu item.
    /// </summary>
    public Func<Task>? UserSettingsAction { get; set; }

    /// <summary>
    /// Gets or sets the host-owned action invoked by the sign-out menu item.
    /// </summary>
    public Func<Task>? UserLogoutAction { get; set; }

    /// <summary>
    /// Whether to show the language switcher
    /// </summary>
    public bool ShowLanguageSwitcher { get; set; } = true;

    /// <summary>
    /// Adds an ordered navigation contribution that the Shell applies during its own web startup lifecycle.
    /// </summary>
    /// <param name="contribution">
    /// A write-only contribution that registers pages or categories through the current host's navigation builder.
    /// </param>
    /// <remarks>
    /// UI modules should call this from their <c>Describe</c> dependency contribution. Contributions are applied in
    /// composition order before the Shell seals its page catalog, so direct and transitive inclusion are equivalent.
    /// </remarks>
    public void ConfigureNavigation(Action<INavigationRegistryBuilder> contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _navigationContributions.Add(contribution);
    }

    internal void ApplyNavigation(INavigationRegistryBuilder navigation)
    {
        foreach (var contribution in _navigationContributions)
        {
            contribution(navigation);
        }
    }
}
