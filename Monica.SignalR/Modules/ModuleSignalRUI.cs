using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.SignalR.Localization;
using Monica.SignalR.Pages;
using Monica.SignalR.UISignalR.State;
using Monica.SignalR.UISignalR.Support;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions used to register the SignalR debug UI module.
/// </summary>
public static class ModuleSignalRUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the SignalR debug UI module and applies optional module configuration.
        /// </summary>
        /// <param name="action">Optional module option configuration delegate.</param>
        /// <returns>The host-bound SignalR UI module registration.</returns>
        public ModuleRegistration<ModuleSignalRUI, ModuleSignalRUIOption> AddSignalRUI(Action<ModuleSignalRUIOption>? action = null)
        {
            return builder.AddModule<ModuleSignalRUI, ModuleSignalRUIOption>(action);
        }
    }
}

/// <summary>
/// UI module that contributes the SignalR debug page and its supporting client-side state services.
/// </summary>
public class ModuleSignalRUI : MonicaModule<ModuleSignalRUIOption>, IUIModule
{
    public override void ConfigureServices(ModuleContext<ModuleSignalRUIOption> context)
    {
        var services = context.Services;
        services.AddScoped<SignalRDebugPageStateFactory>();
        services.AddScoped<SignalRInvocationArgumentParser>();
        services.AddSingleton<SignalRDebugTestTokenService>();
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleSignalR, ModuleSignalROption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<SignalRResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<SignalRResource>(
                    "Monica.SignalR", "Navigation:Category", 30,
                    parentId: BuiltInNavigationCategoryIds.Debug,
                    icon: MoIcons.Network);

                registry.RegisterLocalizedPage<UISignalRDebugPage, SignalRResource>(
                    UISignalRDebugPage.PAGE_URL,
                    "Pages:SignalRDebug:Title",
                    MoIcons.Settings,
                    category,
                    addToNav: true,
                    navOrder: 20);
            }));
    }
}

/// <summary>
/// Registration extensions for the SignalR debug UI module.
/// </summary>


/// <summary>
/// Configuration options for the SignalR debug UI module.
/// </summary>
public class ModuleSignalRUIOption : ModuleOptions<ModuleSignalRUI>
{
    /// <summary>
    /// Gets or sets the default bearer token prefilled on the SignalR debug page before a browser connection is opened.
    /// </summary>
    public string? DefaultAccessToken { get; set; }

    /// <summary>
    /// Gets or sets whether the SignalR debug page may mint a random test-user access token for local debugging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is <see langword="null"/>, which enables the capability only when the host runs in the Development
    /// environment; set <see langword="true"/> or <see langword="false"/> to force the behavior in any environment.
    /// The capability also requires the Monica authentication module to be registered.
    /// </para>
    /// <para>
    /// The minted token is a fully valid JWT for a random user identity, so enabling it on a publicly reachable host
    /// effectively bypasses authentication. Keep it disabled outside trusted debugging environments.
    /// </para>
    /// </remarks>
    public bool? EnableTestUserConnection { get; set; }
}
