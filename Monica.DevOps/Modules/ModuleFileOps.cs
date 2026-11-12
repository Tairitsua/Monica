using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.DevOps.FileOps.Abstractions;
using Monica.DevOps.FileOps.Facades;
using Monica.DevOps.Localization;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.Services;
using Monica.DevOps.FileOps.Services.Support;
using Monica.Core.Results;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public class ModuleFileOps : MonicaModule<ModuleFileOpsOption>, IWebModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization, ModuleLocalizationOption>();
    }

    public override void ConfigureServices(ModuleContext<ModuleFileOpsOption> context)
    {
        var services = context.Services;
        services.AddSingleton<IFileOpsRuntimeConfigStore, FileOpsRuntimeConfigStore>();
        services.AddSingleton<FileOpsPathPolicy>();
        services.AddSingleton<FileOpsTextInspector>();
        services.AddScoped<FileOpsMessageLocalizer>();
        services.AddScoped<FileOpsWorkspaceService>();
        services.AddScoped<FileOpsTransferService>();
        services.AddScoped<IFileOpsWorkspaceService>(provider => provider.GetRequiredService<FileOpsWorkspaceService>());
        services.AddScoped<IFileOpsTransferService>(provider => provider.GetRequiredService<FileOpsTransferService>());
        services.AddScoped<FileOpsFacade>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleFileOpsOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/file-ops/runtime-config",
                    async ([FromServices] FileOpsFacade facade, CancellationToken cancellationToken) =>
                        (await facade.GetRuntimeConfigAsync(cancellationToken)).GetResponse())
                .WithName("GetFileOpsRuntimeConfig")
                .WithTags(tagName)
                .WithSummary("Gets the current FileOps runtime configuration")
                .WithDescription("Returns the current runtime configuration used by the mixed Monica FileOps module.");

            endpoints.MapPut("/file-ops/runtime-config",
                    async ([FromBody] FileOpsRuntimeConfig runtimeConfig,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.SaveRuntimeConfigAsync(runtimeConfig, cancellationToken)).GetResponse())
                .WithName("UpdateFileOpsRuntimeConfig")
                .WithTags(tagName)
                .WithSummary("Updates the current FileOps runtime configuration")
                .WithDescription("Replaces the in-memory runtime configuration that constrains allowed roots and file operation limits.");

            endpoints.MapGet("/file-ops/browse",
                    async ([FromQuery] string? path,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.BrowseAsync(path, cancellationToken)).GetResponse())
                .WithName("BrowseFileOpsPath")
                .WithTags(tagName)
                .WithSummary("Browses a directory inside the allowed roots")
                .WithDescription("Lists directories and files for the requested path while enforcing the current FileOps runtime policy.");

            endpoints.MapGet("/file-ops/text-content",
                    async ([FromQuery] string path,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ReadTextFileAsync(path, cancellationToken)).GetResponse())
                .WithName("GetFileOpsTextContent")
                .WithTags(tagName)
                .WithSummary("Reads a text file inside the allowed roots")
                .WithDescription("Loads a text file for emergency editing after validating the size limit and text encoding heuristics.");

            endpoints.MapPut("/file-ops/text-content",
                    async ([FromBody] FileOpsTextUpdateRequest request,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.SaveTextFileAsync(request, cancellationToken)).GetResponse())
                .WithName("UpdateFileOpsTextContent")
                .WithTags(tagName)
                .WithSummary("Saves a text file")
                .WithDescription("Writes a text file back to disk when the current runtime policy allows editing.");

            endpoints.MapPost("/file-ops/directories",
                    async ([FromBody] FileOpsCreateDirectoryRequest request,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.CreateDirectoryAsync(request, cancellationToken)).GetResponse())
                .WithName("CreateFileOpsDirectory")
                .WithTags(tagName)
                .WithSummary("Creates a directory")
                .WithDescription("Creates a new directory under the requested parent path when the current runtime policy allows writes.");

            endpoints.MapPost("/file-ops/delete",
                    async ([FromBody] FileOpsDeleteRequest request,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.DeleteEntryAsync(request, cancellationToken)).GetResponse())
                .WithName("DeleteFileOpsEntry")
                .WithTags(tagName)
                .WithSummary("Deletes a file system entry")
                .WithDescription("Deletes a file or directory when the current runtime policy allows destructive operations.");

            endpoints.MapPost("/file-ops/upload",
                    async ([FromQuery] string directoryPath,
                        [FromQuery] bool overwrite,
                        HttpRequest request,
                        [FromServices] FileOpsFacade facade,
                        CancellationToken cancellationToken) =>
                    {
                        var form = await request.ReadFormAsync(cancellationToken);
                        var payloads = new List<FileOpsUploadPayload>(form.Files.Count);
                        try
                        {
                            foreach (var formFile in form.Files)
                            {
                                payloads.Add(new FileOpsUploadPayload(formFile.FileName, formFile.Length, formFile.OpenReadStream()));
                            }

                            return (await facade.UploadAsync(directoryPath, payloads, overwrite, cancellationToken)).GetResponse();
                        }
                        finally
                        {
                            foreach (var payload in payloads)
                            {
                                payload.Content.Dispose();
                            }
                        }
                    })
                .DisableAntiforgery()
                .WithName("UploadFileOpsFiles")
                .WithTags(tagName)
                .WithSummary("Uploads one or more files")
                .WithDescription("Writes uploaded files into the target directory after validating the current runtime file operation policy.");

            endpoints.MapGet("/file-ops/download",
                    DownloadAsync)
                .WithName("DownloadFileOpsEntry")
                .WithTags(tagName)
                .WithSummary("Downloads a file")
                .WithDescription("Streams a file from an allowed root when the current runtime policy permits the requested download size.");

            endpoints.MapPost("/file-ops/download/archive",
                    DownloadArchiveAsync)
                .DisableAntiforgery()
                .WithName("DownloadFileOpsArchive")
                .WithTags(tagName)
                .WithSummary("Downloads selected entries as a ZIP archive")
                .WithDescription("Streams a ZIP archive for selected files and directories while enforcing the current FileOps runtime policy.");
        });
    }

    private static async Task<IResult> DownloadAsync(
        [FromQuery] string path,
        [FromServices] IFileOpsTransferService transferService,
        [FromServices] FileOpsMessageLocalizer messageLocalizer,
        [FromServices] IStringLocalizer<FileOpsResource> localizer,
        [FromServices] ILogger<ModuleFileOps> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await transferService.OpenDownloadAsync(path, cancellationToken);
            return Results.File(
                descriptor.ContentStream,
                descriptor.ContentType,
                descriptor.FileName,
                lastModified: descriptor.LastModifiedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download FileOps file {Path}.", path);
            var response = Result.Failure(
                localizer["ServiceMessages:DownloadFailed", path, messageLocalizer.TranslateExceptionMessage(ex)].Value,
                GetResultStatus(ex));
            return response.GetResponse();
        }
    }

    private static async Task<IResult> DownloadArchiveAsync(
        [FromBody] FileOpsArchiveDownloadRequest request,
        [FromServices] IFileOpsTransferService transferService,
        [FromServices] FileOpsMessageLocalizer messageLocalizer,
        [FromServices] IStringLocalizer<FileOpsResource> localizer,
        [FromServices] ILogger<ModuleFileOps> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptor = await transferService.OpenArchiveDownloadAsync(request, cancellationToken);
            return Results.File(
                descriptor.ContentStream,
                descriptor.ContentType,
                descriptor.FileName,
                lastModified: descriptor.LastModifiedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download FileOps archive with {PathCount} selected path(s).", request.Paths?.Count ?? 0);
            var response = Result.Failure(
                localizer["ServiceMessages:ArchiveDownloadFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
                GetResultStatus(ex));
            return response.GetResponse();
        }
    }

    private static ResultStatus GetResultStatus(Exception exception)
    {
        return exception switch
        {
            ArgumentException => ResultStatus.BadRequest,
            InvalidOperationException => ResultStatus.BadRequest,
            KeyNotFoundException => ResultStatus.BadRequest,
            DirectoryNotFoundException => ResultStatus.BadRequest,
            FileNotFoundException => ResultStatus.BadRequest,
            IOException => ResultStatus.BadRequest,
            UnauthorizedAccessException => ResultStatus.Forbidden,
            DevOps.FileOps.Exceptions.FileOpsOperationException => ResultStatus.BadRequest,
            _ => ResultStatus.InternalError
        };
    }
}

public static class ModuleFileOpsBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        public ModuleRegistration<ModuleFileOps, ModuleFileOpsOption> AddFileOps(Action<ModuleFileOpsOption>? action = null)
        {
            var module = builder.AddModule<ModuleFileOps, ModuleFileOpsOption>(action);
            module.Require<ModuleLocalization, ModuleLocalizationOption>()
                .AddResource<FileOpsResource>();
            return module;
        }
    }
}



public class ModuleFileOpsOption : MinimalApiModuleOptions<ModuleFileOps>
{
    public FileOpsRuntimeConfig RuntimeConfig { get; set; } = new();
}
