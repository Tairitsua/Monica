using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.UI.Icons;
using Monica.UI.Localization;
using Monica.UI.Shared.Components.Markdown;
using Monica.UI.Shell.State;
using Monica.UI.Shell.Support;
using Monica.UI.Theming;
using MudBlazor;
using MudBlazor.Services;

namespace Monica.Modules;

/// <summary>Registers reusable UI services without a router, document root, or navigation shell.</summary>
public static class ModuleUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Configures shared themes, MudBlazor services, browser preferences, and Markdown support.</summary>
        public ModuleRegistration<ModuleUI, ModuleUIOption> AddUI(Action<ModuleUIOption>? configure = null)
            => builder.AddModule<ModuleUI, ModuleUIOption>(configure);
    }
}

/// <summary>Provides circuit-owned UI primitives. The host owns rendering and provider placement.</summary>
public class ModuleUI : MonicaModule<ModuleUIOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
        => module.Require<ModuleLocalization, ModuleLocalizationOption>(options => options.AddResource<SharedResource>());

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleUIOption> context)
    {
        var services = context.Services;
        services.AddMudServices(options =>
        {
            options.SnackbarConfiguration.NormalIcon = MoIcons.CalendarDays;
            options.SnackbarConfiguration.InfoIcon = MoIcons.Info;
            options.SnackbarConfiguration.SuccessIcon = MoIcons.Success;
            options.SnackbarConfiguration.WarningIcon = MoIcons.Warning;
            options.SnackbarConfiguration.ErrorIcon = MoIcons.Error;
        });
        if (Option.EnableMarkdown)
        {
            services.AddMudMarkdownServices();
        }

        services.AddScoped<IBrowserStorage, BrowserStorage>();
        services.AddScoped<ThemeState>();
        services.AddScoped<IThemeState>(provider => provider.GetRequiredService<ThemeState>());
        services.TryAddScoped<IMoMarkdownAssetResolver, PassThroughMarkdownAssetResolver>();
    }
}

/// <summary>Shared UI defaults, independent of the product's layout and navigation.</summary>
public class ModuleUIOption : ModuleOptions<ModuleUI>
{
    /// <summary>Enables Markdown services and assets. Defaults to false.</summary>
    public bool EnableMarkdown { get; set; }

    /// <summary>Initial theme when no browser preference exists. Defaults to Default.</summary>
    public MonicaThemeKind DefaultTheme { get; set; } = MonicaThemeKind.Default;

    /// <summary>Initial dark mode when no browser preference exists. Defaults to false.</summary>
    public bool DefaultDarkMode { get; set; }
}
