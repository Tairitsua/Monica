using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.DevOps.FileOps.Abstractions;
using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.Localization;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.Services.Support;
using Monica.Core.Results;

namespace Monica.DevOps.FileOps.Facades;

/// <summary>Projects the scoped FileOps filesystem and transfer boundaries into result envelopes.</summary>
/// <remarks>Injected services are borrowed. Callers own upload input streams and successful download streams.
/// Service failures are returned as failed envelopes; cancellation propagates. A failed or canceled mutation can
/// already have applied filesystem changes. Native UI retirement must therefore observe actual producer completion.</remarks>
public class FileOpsFacade(
    IFileOpsWorkspaceService workspaceService,
    IFileOpsTransferService transferService,
    FileOpsMessageLocalizer messageLocalizer,
    IStringLocalizer<FileOpsResource> localizer,
    ILogger<FileOpsFacade> logger)
{
    public Task<Result<FileOpsRuntimeConfig>> GetRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => workspaceService.GetRuntimeConfigAsync(cancellationToken),
            ex => localizer["ServiceMessages:LoadRuntimeConfigFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            "Failed to load FileOps runtime configuration.");
    }

    public Task<Result> SaveRuntimeConfigAsync(FileOpsRuntimeConfig runtimeConfig, CancellationToken cancellationToken = default)
    {
        var normalizedConfig = runtimeConfig.Clone().Normalize();
        return ExecuteAsync(
            () => workspaceService.UpdateRuntimeConfigAsync(normalizedConfig, cancellationToken),
            () => messageLocalizer.GetRuntimeConfigSavedMessage(normalizedConfig),
            ex => localizer["ServiceMessages:SaveRuntimeConfigFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            "Failed to save FileOps runtime configuration.");
    }

    public Task<Result<FileOpsBrowseResult>> BrowseAsync(string? path, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => workspaceService.BrowseAsync(path, cancellationToken),
            ex => localizer["ServiceMessages:BrowseFailed", path ?? string.Empty, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to browse FileOps path {path ?? "(default)"}.");
    }

    public Task<Result<FileOpsFileTextContent>> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => workspaceService.ReadTextFileAsync(path, cancellationToken),
            ex => localizer["ServiceMessages:ReadTextFailed", path, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to read FileOps text file {path}.");
    }

    public Task<Result> SaveTextFileAsync(FileOpsTextUpdateRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => workspaceService.SaveTextFileAsync(request, cancellationToken),
            () => messageLocalizer.GetTextFileSavedMessage(request.Path),
            ex => localizer["ServiceMessages:SaveTextFailed", request.Path, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to save FileOps text file {request.Path}.");
    }

    public Task<Result> CreateDirectoryAsync(FileOpsCreateDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            async () =>
            {
                var createdPath = await workspaceService.CreateDirectoryAsync(request, cancellationToken);
                return messageLocalizer.GetDirectoryCreatedMessage(createdPath);
            },
            ex => localizer["ServiceMessages:CreateDirectoryFailed", request.ParentPath, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to create FileOps directory in {request.ParentPath}.");
    }

    public Task<Result> DeleteEntryAsync(FileOpsDeleteRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            async () =>
            {
                var result = await workspaceService.DeleteEntryAsync(request, cancellationToken);
                return messageLocalizer.GetEntryDeletedMessage(result);
            },
            ex => localizer["ServiceMessages:DeleteEntryFailed", request.Path, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to delete FileOps entry {request.Path}.");
    }

    public Task<Result<FileOpsDownloadDescriptor>> OpenDownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => transferService.OpenDownloadAsync(path, cancellationToken),
            ex => localizer["ServiceMessages:DownloadFailed", path, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to open FileOps download stream for {path}.");
    }

    public Task<Result<FileOpsDownloadDescriptor>> OpenArchiveDownloadAsync(
        FileOpsArchiveDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => transferService.OpenArchiveDownloadAsync(request, cancellationToken),
            ex => localizer["ServiceMessages:ArchiveDownloadFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to open FileOps archive download stream for {request.Paths?.Count ?? 0} entries.");
    }

    public Task<Result<FileOpsBatchDeleteResult>> DeleteEntriesAsync(
        FileOpsBatchDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => workspaceService.DeleteEntriesAsync(request, cancellationToken),
            ex => localizer["ServiceMessages:BatchDeleteFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to batch delete {request.Paths.Count} FileOps entries.");
    }

    public Task<Result<FileOpsUploadResult>> UploadAsync(
        string directoryPath,
        IReadOnlyCollection<FileOpsUploadPayload> files,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            async () =>
            {
                if (files.Count == 0)
                {
                    throw FileOpsOperationException.UploadFilesRequired();
                }

                var uploadedEntries = new List<FileOpsEntrySummary>(files.Count);
                foreach (var file in files)
                {
                    uploadedEntries.Add(await transferService.SaveUploadAsync(
                        directoryPath,
                        file.FileName,
                        file.Length,
                        file.Content,
                        overwrite,
                        cancellationToken));
                }

                return new FileOpsUploadResult
                {
                    DirectoryPath = directoryPath,
                    Overwrite = overwrite,
                    UploadedEntries = uploadedEntries
                };
            },
            ex => localizer["ServiceMessages:UploadFailed", directoryPath, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to upload FileOps files into {directoryPath}.");
    }

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<Task<T>> action,
        Func<Exception, string> failureMessageFactory,
        string logMessage)
    {
        try
        {
            return Result.Success(await action());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "{LogMessage}", logMessage);
            return Result.Failure<T>(failureMessageFactory(ex), GetResultStatus(ex));
        }
    }

    private async Task<Result> ExecuteAsync(
        Func<Task> action,
        Func<string> successMessageFactory,
        Func<Exception, string> failureMessageFactory,
        string logMessage)
    {
        try
        {
            await action();
            return Result.Success().WithMessage(successMessageFactory());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "{LogMessage}", logMessage);
            return Result.Failure(failureMessageFactory(ex), GetResultStatus(ex));
        }
    }

    private async Task<Result> ExecuteAsync(
        Func<Task<string>> action,
        Func<Exception, string> failureMessageFactory,
        string logMessage)
    {
        try
        {
            return Result.Success().WithMessage(await action());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "{LogMessage}", logMessage);
            return Result.Failure(failureMessageFactory(ex), GetResultStatus(ex));
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
            FileOpsOperationException => ResultStatus.BadRequest,
            _ => ResultStatus.InternalError
        };
    }
}
