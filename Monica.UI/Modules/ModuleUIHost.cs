using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.UI.Hosting;
using Monica.UI.Shell.Components;
using Monica.UI.Shell.Support;

namespace Monica.Modules;

/// <summary>Composes an Interactive Server UI without requiring a business UI module.</summary>
public static class ModuleUIHostBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Uses the shared HTML document and router. No navigation shell is installed.</summary>
        public ModuleRegistration<ModuleUIHost, ModuleUIHostOption> AddUIHost(Action<ModuleUIHostOption>? configure = null)
            => builder.AddModule<ModuleUIHost, ModuleUIHostOption>(configure);

        /// <summary>Uses a host-owned document root. It must render UIProviders once around its interactive router.</summary>
        public ModuleRegistration<ModuleUIHost, ModuleUIHostOption> AddUIHost<TRoot>(Action<ModuleUIHostOption>? configure = null)
            where TRoot : IComponent
            => builder.AddUIHost(options =>
            {
                options.MapRoot = (app, assemblies) => app.MapRazorComponents<TRoot>()
                    .AddInteractiveServerRenderMode()
                    .AddAdditionalAssemblies(assemblies.Where(assembly => assembly != typeof(TRoot).Assembly).ToArray())
                    .WithMonicaEndpoint(MonicaEndpointKind.Ui);
                options.AddPageAssembly(typeof(TRoot).Assembly);
                configure?.Invoke(options);
            });
    }

    extension(ModuleRegistration<ModuleUIHost, ModuleUIHostOption> registration)
    {
        /// <summary>Adds a local redirect mapped alongside UI endpoints. Duplicate source routes are rejected.</summary>
        public ModuleRegistration<ModuleUIHost, ModuleUIHostOption> AddRouteRedirect(string fromPath, string toPath)
            => registration.Configure(options => options.RouteRedirects.Add(fromPath, toPath));
    }
}

/// <summary>Owns Razor hosting, static assets, page discovery, and one endpoint mapping per host.</summary>
public class ModuleUIHost : MonicaModule<ModuleUIHostOption>, IWebHostRequiredModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module) => module.Require<ModuleUI, ModuleUIOption>();

    /// <inheritdoc />
    public override void ConfigureBuilder(ModuleBuilderContext<ModuleUIHostOption> context)
    {
        if (context.HostApplicationBuilder is WebApplicationBuilder builder && !builder.Environment.IsProduction())
        {
            builder.WebHost.UseStaticWebAssets();
        }
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleUIHostOption> context)
    {
        if (!typeof(LayoutComponentBase).IsAssignableFrom(Option.DefaultLayout))
        {
            throw new InvalidOperationException("The default UI layout must derive from LayoutComponentBase.");
        }

        context.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = Option.EnableDebug)
            .AddHubOptions(options => options.EnableDetailedErrors = Option.EnableDebug);
        context.Services.AddSingleton<PageRegistry>();
        context.Services.AddSingleton<INavigationRegistryBuilder>(provider => provider.GetRequiredService<PageRegistry>());
        context.Services.AddSingleton<IPageCatalog>(provider => provider.GetRequiredService<PageRegistry>());
        context.Services.AddScoped<PageAccessEvaluator>();
    }

    /// <inheritdoc />
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleUIHostOption> context)
    {
        var registry = context.ApplicationBuilder.ApplicationServices.GetRequiredService<PageRegistry>();
        foreach (var assembly in Option.PageAssemblies)
        {
            registry.AddAssembly(assembly);
        }

        context.RequireWebApplication().MapStaticAssets().WithMonicaEndpoint(MonicaEndpointKind.StaticAsset);
        context.ApplicationBuilder.UseAntiforgery();
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleUIHostOption> context)
    {
        var app = context.RequireWebApplication();
        var registry = app.Services.GetRequiredService<PageRegistry>();
        registry.Seal();
        foreach (var (from, to) in Option.RouteRedirects)
        {
            app.MapGet(from, () => Results.LocalRedirect(to)).WithMonicaEndpoint(MonicaEndpointKind.Ui);
        }

        Option.MapRoot(app, registry.GetAdditionalAssemblies());
    }

    /// <inheritdoc />
    protected override ModuleWebStage GetApplicationBuilderStage() => ModuleWebStage.AfterRouting;
}

/// <summary>Configures page discovery and Interactive Server hosting independently of navigation.</summary>
public class ModuleUIHostOption : ModuleOptions<ModuleUIHost>
{
    internal HashSet<Assembly> PageAssemblies { get; } = [];
    internal Dictionary<string, string> RouteRedirects { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Type? CatalogPageLayout { get; set; }
    internal Action<WebApplication, Assembly[]> MapRoot { get; set; } = (app, assemblies) =>
        app.MapRazorComponents<AppShell>().AddInteractiveServerRenderMode().AddAdditionalAssemblies(assemblies)
            .WithMonicaEndpoint(MonicaEndpointKind.Ui);

    /// <summary>Default layout for product pages. Defaults to a layout that renders only its body.</summary>
    /// <remarks>Native @layout declarations take precedence. Shell-registered destinations use the Shell layout.</remarks>
    public Type DefaultLayout { get; set; } = typeof(BodyLayout);

    /// <summary>Enables detailed circuit and hub errors. Defaults to false; avoid enabling in production.</summary>
    public bool EnableDebug { get; set; }

    /// <summary>Discovers routes for both navigation and direct HTTP refresh, without creating menu entries.</summary>
    public void AddPageAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        PageAssemblies.Add(assembly);
    }
}
