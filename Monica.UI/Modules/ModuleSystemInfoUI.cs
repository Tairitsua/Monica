using Monica.UI.Icons;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Localization.Models;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.UI.Localization;
using Monica.UI.Pages;
using Monica.UI.Shell.Models;
using Monica.UI.UISystemInfo.Facades;
using Monica.UI.UISystemInfo.Models;
using Monica.UI.UISystemInfo.State;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleSystemInfoUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the SystemInfoUI module.
        /// </summary>
        public ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> AddSystemInfoUI(
            Action<ModuleSystemInfoUIOption>? action = null)
        {
            return builder.AddModule<ModuleSystemInfoUI, ModuleSystemInfoUIOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> registration)
    {
        /// <summary>
        /// Adds a custom shortcut link to the system information page.
        /// </summary>
        /// <param name="name">Display name of the link.</param>
        /// <param name="url">Link URL. Supports relative paths such as "/swagger" and absolute URLs.</param>
        /// <param name="icon">MudBlazor Material icon string. Defaults to the generic link icon.</param>
        /// <param name="description">Optional description shown under the link.</param>
        /// <param name="category">Optional grouping label used to organize shortcuts.</param>
        /// <param name="order">Display order. Smaller values are shown first.</param>
        /// <param name="target">Target behavior used by the generated anchor element.</param>
        /// <param name="userName">Optional user name shown for quick copy.</param>
        /// <param name="password">Optional password exposed only through a copy action.</param>
        public ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> AddCustomLink(
            string name,
            string url,
            string? icon = null,
            string? description = null,
            string? category = null,
            int order = 0,
            SystemInfoCustomLinkTarget target = SystemInfoCustomLinkTarget.NewTab,
            string? userName = null,
            string? password = null)
        {
            return AddCustomLinkCore(registration, new SystemInfoCustomLink
            {
                Name = LocalizedText.Plain(name),
                Url = url,
                Icon = icon ?? MoIcons.Link,
                Description = LocalizedText.PlainOrNull(description),
                Category = LocalizedText.PlainOrNull(category),
                Order = order,
                Target = target,
                UserName = userName,
                Password = password
            });
        }

        /// <summary>
        /// Adds a localized quick link that opens the Swagger UI page from system information.
        /// This helper currently assumes Swagger UI is exposed at <c>/swagger</c>.
        /// </summary>
        /// <param name="order">Display order. Smaller values are shown first.</param>
        /// <param name="target">Target behavior used by the generated anchor element.</param>
        /// <returns>The same host-bound registration.</returns>
        public ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> AddSwaggerLink(
            int order = 0,
            SystemInfoCustomLinkTarget target = SystemInfoCustomLinkTarget.NewTab)
        {
            return AddCustomLinkCore(registration, new SystemInfoCustomLink
            {
                Name = LocalizedText.Resource(SWAGGER_LINK_NAME_KEY, "API Management"),
                // TODO: Replace this hardcoded route after the Swagger module exposes its UI path as an option.
                Url = "/swagger",
                Icon = MoIcons.Plug,
                Description = LocalizedText.Resource(SWAGGER_LINK_DESCRIPTION_KEY, "View and test API endpoints."),
                Category = LocalizedText.Resource(SWAGGER_LINK_CATEGORY_KEY, "Monica"),
                Order = order,
                Target = target
            });
        }

        /// <summary>
        /// Enables the self-restart action on the system information page and exposes the matching API endpoint.
        /// This action only requests graceful shutdown through <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication" />.
        /// The process restarts only when an external supervisor such as Kubernetes, systemd, IIS, or the Windows Service Control Manager restarts it.
        /// </summary>
        /// <param name="shutdownDelay">
        /// Optional delay before shutdown begins. Use a short positive delay to give the initiating HTTP request or Blazor event
        /// time to complete and flush feedback to the user before the host starts shutting down.
        /// </param>
        /// <returns>The same host-bound registration.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="shutdownDelay" /> is negative.</exception>
        public ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> EnableSelfRestartAction(
            TimeSpan? shutdownDelay = null)
        {
            if (shutdownDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(shutdownDelay), shutdownDelay, "Shutdown delay must be zero or positive.");
            }

            return registration.Configure(option =>
            {
                option.EnableSelfRestartAction = true;

                if (shutdownDelay.HasValue)
                {
                    option.SelfRestartDelay = shutdownDelay.Value;
                }
            });
        }
    }

    private const string SWAGGER_LINK_NAME_KEY = "CustomLinks:Items:Swagger:Name";
    private const string SWAGGER_LINK_DESCRIPTION_KEY = "CustomLinks:Items:Swagger:Description";
    private const string SWAGGER_LINK_CATEGORY_KEY = "CustomLinks:Items:Swagger:Category";

    private static ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> AddCustomLinkCore(
        ModuleRegistration<ModuleSystemInfoUI, ModuleSystemInfoUIOption> registration,
        SystemInfoCustomLink link)
    {
        return registration.Configure(option => option.CustomLinks.Add(link));
    }
}

/// <summary>
/// System information UI module.
/// </summary>
public class ModuleSystemInfoUI : MonicaModule<ModuleSystemInfoUIOption>, IWebHostRequiredModule, IUIModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<SystemInfoResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<SystemInfoResource>(
                    "Monica.UI.SystemInfo", "Navigation:Category", 5,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.Info);

                registry.RegisterLocalizedPage<UISystemInfoPage, SystemInfoResource>(
                    UISystemInfoPage.PAGE_URL,
                    "Pages:SystemInfo:Title",
                    MoIcons.Info,
                    category,
                    addToNav: true,
                    navOrder: 50,
                    descriptionKey: "Page:Summary");
            }));
    }

    /// <summary>
    /// Registers the services required by the system information UI.
    /// </summary>
    public override void ConfigureServices(ModuleContext<ModuleSystemInfoUIOption> context)
    {
        var services = context.Services;
        // The singleton facade owns the host-wide restart latch and its immutable snapshot provider.
        services.TryAddSingleton<SystemInfoFacade>();
        services.TryAddSingleton<SystemInfoPageSessionFactory>();
    }

    /// <summary>
    /// Maps the system information API endpoints.
    /// </summary>
    public override void ConfigureEndpoints(WebModuleContext<ModuleSystemInfoUIOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/system/info",
                ([FromServices] SystemInfoFacade systemInfoFacade) =>
                    systemInfoFacade.GetSnapshot().GetResponse())
                .WithName("获取微服务信息")
                .WithTags(tagName)
                .WithSummary("获取微服务信息")
                .WithDescription("获取微服务信息");

            if (Option.EnableSelfRestartAction)
            {
                endpoints.MapPost("/system/restart",
                    ([FromServices] SystemInfoFacade systemInfoFacade) =>
                        systemInfoFacade.RequestSelfRestart().GetResponse())
                    .WithName("Request service self restart")
                    .WithTags(tagName)
                    .WithSummary("Requests a graceful self restart for the current service.")
                    .WithDescription("Requests graceful shutdown through IHostApplicationLifetime.StopApplication(). The process only starts again when the host environment has an external restart policy.");
            }
        });
    }
}

/// <summary>
/// SystemInfoUI module options.
/// </summary>
public class ModuleSystemInfoUIOption : MinimalApiModuleOptions<ModuleSystemInfoUI>
{
    /// <summary>
    /// Gets or sets a value indicating whether the module exposes the self-restart control surface.
    /// When enabled, the system information page shows a restart button and the module maps <c>POST /system/restart</c>.
    /// The action only requests graceful shutdown through <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication" />.
    /// The process returns only when an external host supervisor is configured to restart it.
    /// Defaults to <c>false</c>.
    /// </summary>
    public bool EnableSelfRestartAction { get; set; }

    /// <summary>
    /// Gets or sets the delay between accepting a self-restart request and calling
    /// <see cref="Microsoft.Extensions.Hosting.IHostApplicationLifetime.StopApplication" />.
    /// A short delay allows the triggering HTTP request or Blazor UI event to complete and deliver user feedback before shutdown begins.
    /// Defaults to one second.
    /// </summary>
    public TimeSpan SelfRestartDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the custom shortcut links displayed on the system information page.
    /// </summary>
    public List<SystemInfoCustomLink> CustomLinks { get; set; } = new();
}
