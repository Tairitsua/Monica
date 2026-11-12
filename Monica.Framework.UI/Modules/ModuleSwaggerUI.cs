using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UISwagger.Models;
using Monica.Framework.UI.UISwagger.Support;
using Monica.UI.Theming;
using Monica.WebApi.Swagger.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleSwaggerUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Adds the standalone Swagger workbench, shared Monica themes, and localized native control labels.
        /// </summary>
        /// <remarks>
        /// Requires a Web host and composes Swagger generation and localization. It does not compose the Blazor shell.
        /// Hosts without the shell must expose Razor class library static assets at <c>/_content</c>.
        /// Existing Swagger UI options, middleware, authorization, and request execution remain native.
        /// </remarks>
        public ModuleRegistration<ModuleSwaggerUI, ModuleSwaggerUIOption> AddSwaggerUI(
            Action<ModuleSwaggerUIOption>? action = null)
        {
            return builder.AddModule<ModuleSwaggerUI, ModuleSwaggerUIOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleSwaggerUI, ModuleSwaggerUIOption> registration)
    {
        /// <summary>
        /// Redirects the application root route to the default <c>/swagger</c> UI path through the shell.
        /// </summary>
        /// <remarks>
        /// This helper assumes the default Swagger route prefix. Hosts using another prefix should configure
        /// their root destination explicitly through the shell's route redirects.
        /// </remarks>
        /// <returns>The same host-bound registration.</returns>
        public ModuleRegistration<ModuleSwaggerUI, ModuleSwaggerUIOption> SetAsRootRoute()
        {
            registration.Require<ModuleShellUI, ModuleShellUIOption>()
                .AddRouteRedirect("/", "/swagger");
            return registration;
        }

        /// <summary>
        /// Adds a caller-named destination to the standalone Swagger toolbar.
        /// </summary>
        /// <param name="name">Caller-owned literal link text.</param>
        /// <param name="path">Application-root path or absolute HTTP/HTTPS URL.</param>
        /// <param name="description">Optional caller-owned tooltip.</param>
        /// <param name="order">Display order; smaller values appear first. Defaults to zero.</param>
        public ModuleRegistration<ModuleSwaggerUI, ModuleSwaggerUIOption> AddNavigationButton(
            string name,
            string path,
            string? description = null,
            int order = 0)
        {
            return registration.Configure(option => option.NavigationButtons.Add(new SwaggerNavigationButton
            {
                Name = name,
                Path = path,
                Description = description,
                Order = order
            }));
        }
    }
}

/// <summary>
/// Extends the native Swagger index with theme preferences, localized labels, and optional application navigation.
/// </summary>
public class ModuleSwaggerUI : MonicaModule<ModuleSwaggerUIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<ModuleUI, ModuleUIOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static options => options.AddResource<SwaggerUiResource>());
        module.Require<ModuleSwagger, ModuleSwaggerOption>();
    }

    public override void ConfigureServices(ModuleContext<ModuleSwaggerUIOption> context)
    {
        var buttons = Array.AsReadOnly(Option.NavigationButtons
            .Where(static button => button.Enabled)
            .OrderBy(static button => button.Order)
            .ToArray());
        var defaults = TryGetOptions<ModuleUI, ModuleUIOption>(out var shellOptions)
            && shellOptions is not null
                ? new ThemePreference(shellOptions.DefaultTheme, shellOptions.DefaultDarkMode).Normalize()
                : new ThemePreference(MonicaThemeKind.Default, false);

        context.Services.AddSingleton<ISwaggerUIOptionsEnhancer>(provider => new SwaggerUiIndexRenderer(
            provider.GetRequiredService<IHttpContextAccessor>(),
            provider.GetRequiredService<IStringLocalizer<SwaggerUiResource>>(),
            defaults,
            buttons));
    }
}

/// <summary>
/// Configures optional destinations in the standalone Swagger workbench.
/// </summary>
public class ModuleSwaggerUIOption : ModuleOptions<ModuleSwaggerUI>
{
    /// <summary>
    /// Caller-owned navigation destinations in the Swagger toolbar. Defaults to an empty list.
    /// Disabled entries are omitted, and enabled entries are ordered by <see cref="SwaggerNavigationButton.Order"/>.
    /// The complete themed Swagger workbench is enabled even when this list is empty.
    /// </summary>
    public List<SwaggerNavigationButton> NavigationButtons { get; set; } = new();
}
