using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Markdown.Pages;
using Monica.Markdown.Localization;
using Monica.Markdown.UIMarkdown.Models;
using Monica.Markdown.UIMarkdown.State;
using Monica.Markdown.UIMarkdown.Support;
using Monica.UI.Shared.Components.Markdown;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Markdown UI module providing a document viewer with group selection,
/// tree navigation, and markdown rendering.
/// </summary>
public class ModuleMarkdownUI : MonicaModule<ModuleMarkdownUIOption>, IWebHostRequiredModule, IUIModule
{
    /// <summary>
    /// Configures services for the Markdown UI module.
    /// </summary>
    public override void ConfigureServices(ModuleContext<ModuleMarkdownUIOption> context)
    {
        var services = context.Services;
        services.AddTransient<MarkdownViewerPageStateFactory>();
        services.AddTransient<MarkdownDocumentSearchStateFactory>();
        services.AddTransient<MarkdownLocalAssetService>();
        services.Replace(ServiceDescriptor.Scoped<IMoMarkdownAssetResolver, MarkdownAssetUrlResolver>());
    }

    /// <summary>
    /// Declares module dependencies.
    /// </summary>
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleMarkdown, ModuleMarkdownOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<MarkdownResource>());
        module.Require<ModuleUI, ModuleUIOption>(options => options.EnableMarkdown = true);
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
        {
            option.ConfigureNavigation(static registry =>
                registry.RegisterLocalizedPage<UIMarkdownPage, MarkdownResource>(
                    MarkdownViewerLocation.PAGE_URL,
                    "Pages:MarkdownDocuments:Title",
                    MoIcons.Book,
                    BuiltInNavigationCategoryIds.Documentation,
                    addToNav: true,
                    navOrder: 50));
        });
    }

    /// <summary>
    /// Configures endpoints for local markdown image assets.
    /// </summary>
    public override void ConfigureEndpoints(WebModuleContext<ModuleMarkdownUIOption> context)
    {
        var app = context.ApplicationBuilder;
        if (!Option.EnableLocalImageAssetEndpoint)
        {
            return;
        }

        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();
            var routePattern = NormalizeRoutePattern(Option.AssetEndpointBasePath);

            endpoints.MapGet(routePattern,
                    async ([FromRoute] string groupKey,
                           [FromQuery] string documentRelativePath,
                           [FromQuery] string assetPath,
                           [FromServices] MarkdownLocalAssetService assetService) =>
                    {
                        try
                        {
                            var asset = await assetService.OpenImageAsync(
                                groupKey,
                                documentRelativePath,
                                assetPath);

                            var stream = File.OpenRead(asset.FilePath);
                            return Results.File(stream, asset.ContentType, enableRangeProcessing: true);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException
                                                   or FileNotFoundException
                                                   or DirectoryNotFoundException
                                                   or KeyNotFoundException)
                        {
                            return Results.NotFound(new
                            {
                                Message = ex.Message,
                                GroupKey = groupKey,
                                DocumentRelativePath = documentRelativePath,
                                AssetPath = assetPath
                            });
                        }
                    })
                .WithName("GetMarkdownLocalImageAsset")
                .WithTags(tagName)
                .WithSummary("Gets a local markdown image asset")
                .WithDescription("Streams a validated local image referenced by a markdown document.");
        });
    }

    private static string NormalizeRoutePattern(string? basePath)
    {
        var trimmed = string.IsNullOrWhiteSpace(basePath)
            ? "/markdown-ui/assets"
            : basePath.Trim();

        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return $"{trimmed.TrimEnd('/')}/{{groupKey}}";
    }
}

public static class ModuleMarkdownUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Markdown UI module.
        /// </summary>
        public ModuleRegistration<ModuleMarkdownUI, ModuleMarkdownUIOption> AddMarkdownUI(Action<ModuleMarkdownUIOption>? action = null)
        {
            return builder.AddModule<ModuleMarkdownUI, ModuleMarkdownUIOption>(action);
        }
    }
}

/// <summary>
/// Registration extensions for the Markdown UI module.
/// </summary>
/// <summary>
/// Options for the Markdown UI module.
/// </summary>
public class ModuleMarkdownUIOption : MinimalApiModuleOptions<ModuleMarkdownUI>
{
    /// <summary>
    /// Whether the local markdown image asset endpoint is enabled.
    /// </summary>
    public bool EnableLocalImageAssetEndpoint { get; set; } = true;

    /// <summary>
    /// Base path for the local markdown asset endpoint.
    /// </summary>
    public string AssetEndpointBasePath { get; set; } = "/markdown-ui/assets";

    /// <summary>
    /// Client-side debounce delay for search input, in milliseconds.
    /// </summary>
    public int DocumentSearchDebounceMilliseconds { get; set; } = 250;

    /// <summary>
    /// Image extensions allowed to be served by the local markdown asset endpoint.
    /// </summary>
    public string[] AllowedImageExtensions { get; set; } =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".webp",
        ".svg",
        ".bmp",
        ".ico",
        ".avif"
    ];
}
