using Microsoft.Extensions.Logging;
using Monica.DevOps.FileOps.Abstractions;
using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.Services.Support;
using Monica.Tool.Text;

namespace Monica.DevOps.FileOps.Services;

public class FileOpsWorkspaceService(
    IFileOpsRuntimeConfigStore runtimeConfigStore,
    FileOpsPathPolicy pathPolicy,
    FileOpsTextInspector textInspector,
    ILogger<FileOpsWorkspaceService> logger) : IFileOpsWorkspaceService
{
    public Task<FileOpsRuntimeConfig> GetRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(runtimeConfigStore.GetCurrent());
    }

    public Task UpdateRuntimeConfigAsync(FileOpsRuntimeConfig runtimeConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeConfig);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedConfig = runtimeConfig.Clone().Normalize();
        if (normalizedConfig.AllowedRoots.Count == 0)
        {
            throw FileOpsOperationException.AllowedRootsRequired();
        }

        foreach (var rootPath in normalizedConfig.AllowedRoots)
        {
            if (!Directory.Exists(rootPath))
            {
                throw FileOpsOperationException.ConfiguredRootMissing(rootPath);
            }
        }

        runtimeConfigStore.Update(normalizedConfig);
        logger.LogInformation(
            "Updated FileOps runtime configuration. Allowed roots: {AllowedRoots}. Read-only paths: {ReadOnlyPaths}. Write enabled: {AllowWrite}. Delete enabled: {AllowDelete}",
            normalizedConfig.AllowedRoots.Count,
            normalizedConfig.ReadOnlyPaths.Count,
            normalizedConfig.AllowWriteOperations,
            normalizedConfig.AllowDeleteOperations);

        return Task.CompletedTask;
    }

    public Task<FileOpsBrowseResult> BrowseAsync(string? path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtimeConfig = runtimeConfigStore.GetCurrent();
        var directory = pathPolicy.ResolveDirectory(runtimeConfig, path);
        var entries = Directory.EnumerateFileSystemEntries(directory.FullPath)
            .Select(entryPath => CreateEntrySummary(runtimeConfig, entryPath))
            .Where(entry => runtimeConfig.AllowHiddenEntries || !entry.IsHidden)
            .OrderByDescending(static entry => entry.IsDirectory)
            .ThenBy(static entry => entry.Name, GetPathComparer())
            .ToList();

        var limitedEntries = entries.Take(runtimeConfig.MaxDirectoryEntries).ToList();
        foreach (var entry in limitedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservePreview(runtimeConfig, entry);
        }

        return Task.FromResult(new FileOpsBrowseResult
        {
            CurrentPath = directory.FullPath,
            CurrentRootPath = directory.RootPath,
            ParentPath = pathPolicy.GetParentPath(directory),
            Breadcrumbs = pathPolicy.BuildBreadcrumbs(directory),
            Entries = limitedEntries,
            CanCreateDirectory = runtimeConfig.AllowWriteOperations && !pathPolicy.IsReadOnlyPath(runtimeConfig, directory.FullPath),
            CanUpload = runtimeConfig.AllowWriteOperations && !pathPolicy.IsReadOnlyPath(runtimeConfig, directory.FullPath),
            IsReadOnly = pathPolicy.IsReadOnlyPath(runtimeConfig, directory.FullPath),
            IsTruncated = entries.Count > limitedEntries.Count,
            EntryLimit = runtimeConfig.MaxDirectoryEntries
        });
    }

    /// <inheritdoc />
    public Task<FileOpsEntrySummary> ObserveFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = runtimeConfigStore.GetCurrent();
        var file = pathPolicy.ResolveFile(policy, path);
        var entry = CreateEntrySummary(policy, file.FullPath);
        ObservePreview(policy, entry);
        return Task.FromResult(entry);
    }

    private void ObservePreview(FileOpsRuntimeConfig policy, FileOpsEntrySummary entry)
    {
        if (entry.IsDirectory || entry.TextPreview != FileOpsTextPreviewKind.Unknown) return;

        entry.TextPreview = textInspector.Classify(entry.FullPath, entry.SizeBytes!.Value, policy.MaxTextFileSizeBytes);
        entry.CanEditText &= entry.TextPreview == FileOpsTextPreviewKind.Text;
    }

    private FileOpsEntrySummary CreateEntrySummary(FileOpsRuntimeConfig runtimeConfig, string entryPath)
    {
        var attributes = File.GetAttributes(entryPath);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            var directoryInfo = new DirectoryInfo(entryPath);
            return new FileOpsEntrySummary
            {
                Name = directoryInfo.Name,
                FullPath = directoryInfo.FullName,
                Kind = FileOpsEntryKind.Directory,
                LastModifiedAt = directoryInfo.LastWriteTimeUtc,
                IsHidden = pathPolicy.IsHidden(directoryInfo),
                IsReadOnly = pathPolicy.IsReadOnlyPath(runtimeConfig, directoryInfo.FullName),
                CanDelete = runtimeConfig.AllowDeleteOperations && !pathPolicy.IsReadOnlyPath(runtimeConfig, directoryInfo.FullName) &&
                            !runtimeConfig.AllowedRoots.Contains(Path.TrimEndingDirectorySeparator(directoryInfo.FullName), GetPathComparer()),
                CanWriteInto = runtimeConfig.AllowWriteOperations && !pathPolicy.IsReadOnlyPath(runtimeConfig, directoryInfo.FullName)
            };
        }

        var fileInfo = new FileInfo(entryPath);
        return new FileOpsEntrySummary
        {
            Name = fileInfo.Name,
            FullPath = fileInfo.FullName,
            Kind = FileOpsEntryKind.File,
            Extension = fileInfo.Extension,
            SizeBytes = fileInfo.Length,
            LastModifiedAt = fileInfo.LastWriteTimeUtc,
            TextPreview = attributes.HasFlag(FileAttributes.ReparsePoint)
                ? FileOpsTextPreviewKind.Unavailable : FileOpsTextPreviewKind.Unknown,
            IsHidden = pathPolicy.IsHidden(fileInfo),
            IsReadOnly = pathPolicy.IsReadOnlyPath(runtimeConfig, fileInfo.FullName),
            CanDownload = fileInfo.Length <= runtimeConfig.MaxDownloadFileSizeBytes,
            CanEditText = !attributes.HasFlag(FileAttributes.ReparsePoint) &&
                          fileInfo.Length <= runtimeConfig.MaxTextFileSizeBytes &&
                          runtimeConfig.CanEditExtension(fileInfo.Extension) &&
                          runtimeConfig.AllowWriteOperations &&
                          !pathPolicy.IsReadOnlyPath(runtimeConfig, fileInfo.FullName),
            CanDelete = runtimeConfig.AllowDeleteOperations && !pathPolicy.IsReadOnlyPath(runtimeConfig, fileInfo.FullName)
        };
    }

    public async Task<FileOpsFileTextContent> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var runtimeConfig = runtimeConfigStore.GetCurrent();
        var file = pathPolicy.ResolveFile(runtimeConfig, path);
        var readResult = await textInspector.ReadAsync(file.FullPath, runtimeConfig.MaxTextFileSizeBytes, cancellationToken);
        var fileInfo = new FileInfo(file.FullPath);

        return new FileOpsFileTextContent
        {
            FullPath = file.FullPath,
            Name = fileInfo.Name,
            Extension = fileInfo.Extension,
            Content = readResult.Content,
            EncodingName = readResult.EncodingName,
            SizeBytes = fileInfo.Length,
            LastModifiedAt = fileInfo.LastWriteTimeUtc,
            LineCount = readResult.LineCount,
            CanSave = runtimeConfig.AllowWriteOperations &&
                      !pathPolicy.IsReadOnlyPath(runtimeConfig, file.FullPath) &&
                      runtimeConfig.CanEditExtension(fileInfo.Extension),
            CanDownload = fileInfo.Length <= runtimeConfig.MaxDownloadFileSizeBytes,
            IsReadOnly = pathPolicy.IsReadOnlyPath(runtimeConfig, file.FullPath)
        };
    }

    public async Task SaveTextFileAsync(FileOpsTextUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runtimeConfig = runtimeConfigStore.GetCurrent();
        var file = pathPolicy.ResolveFile(runtimeConfig, request.Path);
        if (!runtimeConfig.CanEditExtension(Path.GetExtension(file.FullPath)))
        {
            throw FileOpsOperationException.EditableExtensionRequired(file.FullPath);
        }

        pathPolicy.EnsureEntryWritable(runtimeConfig, file.FullPath);

        var encoding = textInspector.ResolveEncoding(request.EncodingName);
        var bytes = encoding.GetBytes(request.Content ?? string.Empty);
        if (bytes.LongLength > runtimeConfig.MaxTextFileSizeBytes)
        {
            throw FileOpsOperationException.TextFileTooLarge(file.FullPath, runtimeConfig.MaxTextFileSizeBytes.FormatByteSize());
        }

        await File.WriteAllTextAsync(file.FullPath, request.Content ?? string.Empty, encoding, cancellationToken);
        logger.LogInformation("Saved text file via FileOps workspace: {Path}", file.FullPath);
    }

    public Task<string> CreateDirectoryAsync(FileOpsCreateDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runtimeConfig = runtimeConfigStore.GetCurrent();
        var fullPath = pathPolicy.ResolveChildPath(runtimeConfig, request.ParentPath, request.DirectoryName);
        if (Directory.Exists(fullPath) || File.Exists(fullPath))
        {
            throw FileOpsOperationException.EntryAlreadyExists(fullPath);
        }

        Directory.CreateDirectory(fullPath);
        logger.LogInformation("Created directory via FileOps workspace: {Path}", fullPath);
        return Task.FromResult(fullPath);
    }

    public Task<FileOpsDeleteResult> DeleteEntryAsync(FileOpsDeleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var runtimeConfig = runtimeConfigStore.GetCurrent();
        return Task.FromResult(DeleteEntry(runtimeConfig, request.Path, request.Recursive));
    }

    public Task<FileOpsBatchDeleteResult> DeleteEntriesAsync(FileOpsBatchDeleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = GetDistinctPaths(request.Paths);
        if (paths.Count == 0)
        {
            throw FileOpsOperationException.SelectionRequired();
        }

        var runtimeConfig = runtimeConfigStore.GetCurrent();
        var deletedEntries = new List<FileOpsDeleteResult>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deletedEntries.Add(DeleteEntry(runtimeConfig, path, request.Recursive));
        }

        logger.LogInformation("Batch deleted {DeletedCount} FileOps entries.", deletedEntries.Count);
        return Task.FromResult(new FileOpsBatchDeleteResult
        {
            DeletedEntries = deletedEntries
        });
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private FileOpsDeleteResult DeleteEntry(FileOpsRuntimeConfig runtimeConfig, string path, bool recursive)
    {
        var entry = pathPolicy.ResolveExistingEntry(runtimeConfig, path);
        pathPolicy.EnsureEntryDeletable(runtimeConfig, entry);

        if (entry.IsDirectory)
        {
            Directory.Delete(entry.FullPath, recursive);
        }
        else
        {
            File.Delete(entry.FullPath);
        }

        logger.LogInformation("Deleted FileOps entry: {Path}", entry.FullPath);
        return new FileOpsDeleteResult
        {
            Path = entry.FullPath,
            EntryKind = entry.IsDirectory ? FileOpsEntryKind.Directory : FileOpsEntryKind.File
        };
    }

    private static List<string> GetDistinctPaths(IEnumerable<string>? paths)
    {
        return (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Distinct(GetPathComparer())
            .ToList();
    }
}
