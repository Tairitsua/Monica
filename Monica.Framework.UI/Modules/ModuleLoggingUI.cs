using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Framework.UI.UILogging.Models;
using Monica.Framework.UI.Pages;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.State;
using Monica.Framework.UI.UILogging.Support;
using Monica.Framework.UI.Localization;
using Monica.UI.Shell.Models;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleLoggingUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure LoggingUI module
        /// </summary>
        public ModuleRegistration<ModuleLoggingUI, ModuleLoggingUIOption> AddLoggingUI(
            Action<ModuleLoggingUIOption>? action = null)
        {
            return builder.AddModule<ModuleLoggingUI, ModuleLoggingUIOption>(action);
        }
    }
}

/// <summary>
/// Logging UI module implementation
/// </summary>
public class ModuleLoggingUI : MonicaModule<ModuleLoggingUIOption>, IWebHostRequiredModule, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLogging, ModuleLoggingOption>();
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<LoggingResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<LoggingResource>(
                    "Monica.Framework.UI.Logging", "Navigation:Category", 30,
                    parentId: BuiltInNavigationCategoryIds.Monitor,
                    icon: MoIcons.FileText);

                registry.RegisterLocalizedPage<UILoggingMonitorPage, LoggingResource>(
                    UILoggingMonitorPage.PAGE_URL,
                    "Pages:LoggingMonitor:Title",
                    MoIcons.FileText,
                    category,
                    addToNav: true,
                    navOrder: 30,
                    descriptionKey: "Pages:LoggingMonitor:Description");
                registry.RegisterLocalizedPage<UILoggingFilesPage, LoggingResource>(
                    UILoggingFilesPage.PAGE_URL,
                    "Pages:LoggingFiles:Title",
                    MoIcons.FolderOpen,
                    category,
                    addToNav: true,
                    navOrder: 31,
                    descriptionKey: "Pages:LoggingFiles:Description");
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleLoggingUIOption> context)
    {
        var services = context.Services;
        services.AddSingleton<ScreenLogBuffer>();
        services.AddSingleton<LogTailService>();
        services.AddSingleton<LogFileQueryService>();
        services.AddSingleton<LoggingService>();
        services.AddScoped<LoggingWorkspaceSession>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleLoggingUIOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/logging-ui/files",
                async ([FromServices] LoggingService loggingService, HttpContext httpContext) =>
                {
                    var result = await loggingService.ListFilesAsync(httpContext.RequestAborted);
                    return result.GetResponse();
                })
                .WithName("列出日志文件")
                .WithTags(tagName)
                .WithSummary("列出日志文件")
                .WithDescription("获取所有可用的日志文件列表");
        });

        // Downloads are required by the page even when optional Minimal APIs are disabled.
        // Keep Monica ownership metadata so the host's endpoint port policy still applies.
        var downloads = context.RequireWebApplication()
            .MapGroup("/logging-ui")
            .WithMonicaEndpoint(MonicaEndpointKind.Ui)
            .ExcludeFromDescription();

        downloads.MapGet("/files/{*filePath}",
            async ([FromRoute] string filePath,
                  [FromServices] LoggingService loggingService,
                  HttpContext httpContext) =>
            {
                var result = await loggingService.OpenFileAsync(filePath, httpContext.RequestAborted);
                if (result.IsFailed(out var error, out var stream))
                {
                    return error.GetResponse();
                }

                httpContext.Response.RegisterForDisposeAsync(stream);
                var downloadLength = stream.Length;
                var downloadName = Path.GetFileName(filePath);
                // Read only the initial extent. A live log can grow or shrink during transmission,
                // so do not advertise a fixed Content-Length that may disagree with the response body.
                return Results.Stream(
                    output => StreamCopyOperation.CopyToAsync(
                        stream, output, downloadLength, httpContext.RequestAborted),
                    "text/plain",
                    downloadName);
            })
            .WithName("下载日志文件")
            .WithSummary("下载日志文件")
            .WithDescription("下载指定的日志文件");

        downloads.MapGet("/current/export",
            async ([FromServices] LoggingService loggingService, HttpContext httpContext) =>
            {
                var result = await loggingService.ExportBufferAsync(httpContext.RequestAborted);
                if (result.IsFailed(out var error, out var export))
                {
                    return error.GetResponse();
                }

                return Results.File(export.Content, export.ContentType, export.FileName);
            })
            .WithName("导出当前日志")
            .WithSummary("导出当前日志")
            .WithDescription("导出当前缓冲区中的日志");
    }
}

/// <summary>
/// Configures the logging page and its optional file-list Minimal API.
/// </summary>
/// <remarks>
/// The inherited Minimal API switch controls the file-list API only. File downloads and buffer exports
/// are required UI endpoints and remain available whenever this module is registered, subject to the
/// host's Monica endpoint port policy.
/// </remarks>
public class ModuleLoggingUIOption : MinimalApiModuleOptions<ModuleLoggingUI>
{
    /// <summary>
    /// Number of complete physical lines read initially; default 500 and clamped to the effective display limit.
    /// </summary>
    public int DefaultFetchLines { get; set; } = 500;

    /// <summary>
    /// Maximum visible physical lines; default 5,000 and clamped to at least one. Readers retain twice this many raw lines.
    /// </summary>
    public int MaxDisplayLines { get; set; } = 5000;

    /// <summary>
    /// Reader polling interval; default one second. Must be positive and at most 4,294,967,294 milliseconds; positive sub-millisecond values are clamped to one millisecond.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Capture only matches is enabled by default
    /// </summary>
    public bool DefaultOnlyCapture { get; set; } = true;

    /// <summary>
    /// Default number of optional screen log lines
    /// </summary>
    public IReadOnlyList<int> PresetLineCounts { get; set; } = new[] { 200, 500, 1000, 2500, 5000 };

    /// <summary>
    /// Explicit UI inspection directory. Defaults to the resolved Logging file sink directory; custom logger factories must configure their own inspection path.
    /// </summary>
    public string? LogDirectory { get; set; }
}
