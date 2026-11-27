using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.Services.Support;

public sealed record FileOpsResolvedPath(string FullPath, string RootPath, bool Exists, bool IsDirectory);

public class FileOpsPathPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public FileOpsResolvedPath ResolveDirectory(FileOpsRuntimeConfig runtimeConfig, string? candidatePath)
    {
        var fullPath = ResolveFullPath(candidatePath, runtimeConfig.StartupPath);
        var rootPath = MatchRoot(runtimeConfig, fullPath);
        EnsureVisible(runtimeConfig, fullPath, rootPath);

        if (!Directory.Exists(fullPath))
        {
            throw FileOpsOperationException.EntryNotFound(fullPath);
        }

        return new FileOpsResolvedPath(fullPath, rootPath, Exists: true, IsDirectory: true);
    }

    public FileOpsResolvedPath ResolveExistingEntry(FileOpsRuntimeConfig runtimeConfig, string? candidatePath)
    {
        var fullPath = ResolveFullPath(candidatePath, runtimeConfig.StartupPath);
        var rootPath = MatchRoot(runtimeConfig, fullPath);
        EnsureVisible(runtimeConfig, fullPath, rootPath);

        if (Directory.Exists(fullPath))
        {
            return new FileOpsResolvedPath(fullPath, rootPath, Exists: true, IsDirectory: true);
        }

        if (File.Exists(fullPath))
        {
            return new FileOpsResolvedPath(fullPath, rootPath, Exists: true, IsDirectory: false);
        }

        throw FileOpsOperationException.EntryNotFound(fullPath);
    }

    public FileOpsResolvedPath ResolveFile(FileOpsRuntimeConfig runtimeConfig, string? candidatePath)
    {
        var resolved = ResolveExistingEntry(runtimeConfig, candidatePath);
        if (resolved.IsDirectory)
        {
            throw FileOpsOperationException.FileExpected(resolved.FullPath);
        }

        return resolved;
    }

    public string ResolveChildPath(FileOpsRuntimeConfig runtimeConfig, string directoryPath, string entryName)
    {
        var directory = ResolveDirectory(runtimeConfig, directoryPath);
        EnsureDirectoryWritable(runtimeConfig, directory);

        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw FileOpsOperationException.InvalidEntryName(entryName);
        }

        var sanitizedName = Path.GetFileName(entryName.Trim());
        if (string.IsNullOrWhiteSpace(sanitizedName) ||
            sanitizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(sanitizedName, entryName.Trim(), StringComparison.Ordinal))
        {
            throw FileOpsOperationException.InvalidEntryName(entryName);
        }

        var fullPath = Path.GetFullPath(Path.Combine(directory.FullPath, sanitizedName));
        EnsureWithinRoot(directory.RootPath, fullPath);
        EnsureVisible(runtimeConfig, fullPath, directory.RootPath, targetMayNotExist: true);
        return fullPath;
    }

    public string? GetParentPath(FileOpsResolvedPath directory)
    {
        if (!directory.IsDirectory)
        {
            return null;
        }

        var normalizedCurrent = Path.TrimEndingDirectorySeparator(directory.FullPath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(directory.RootPath);
        if (string.Equals(normalizedCurrent, normalizedRoot, PathComparison))
        {
            return null;
        }

        var parent = Directory.GetParent(normalizedCurrent)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        return IsWithinRoot(parent, directory.RootPath) ? Path.GetFullPath(parent) : null;
    }

    public bool IsReadOnlyPath(FileOpsRuntimeConfig runtimeConfig, string fullPath)
    {
        return runtimeConfig.ReadOnlyPaths.Any(readOnlyPath => IsWithinRoot(fullPath, readOnlyPath));
    }

    public void EnsureDirectoryWritable(FileOpsRuntimeConfig runtimeConfig, FileOpsResolvedPath directory)
    {
        if (!runtimeConfig.AllowWriteOperations)
        {
            throw FileOpsOperationException.WriteOperationsDisabled();
        }

        if (IsReadOnlyPath(runtimeConfig, directory.FullPath))
        {
            throw FileOpsOperationException.PathReadOnly(directory.FullPath);
        }
    }

    public void EnsureEntryWritable(FileOpsRuntimeConfig runtimeConfig, string fullPath)
    {
        if (!runtimeConfig.AllowWriteOperations)
        {
            throw FileOpsOperationException.WriteOperationsDisabled();
        }

        if (IsReadOnlyPath(runtimeConfig, fullPath))
        {
            throw FileOpsOperationException.PathReadOnly(fullPath);
        }
    }

    public void EnsureEntryDeletable(FileOpsRuntimeConfig runtimeConfig, FileOpsResolvedPath entry)
    {
        if (!runtimeConfig.AllowDeleteOperations)
        {
            throw FileOpsOperationException.DeleteOperationsDisabled();
        }

        var normalizedEntry = Path.TrimEndingDirectorySeparator(entry.FullPath);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(entry.RootPath);
        if (string.Equals(normalizedEntry, normalizedRoot, PathComparison))
        {
            throw FileOpsOperationException.CannotDeleteRoot(entry.FullPath);
        }

        if (IsReadOnlyPath(runtimeConfig, entry.FullPath))
        {
            throw FileOpsOperationException.PathReadOnly(entry.FullPath);
        }
    }

    public bool IsHidden(FileSystemInfo fileSystemInfo)
    {
        return fileSystemInfo.Attributes.HasFlag(FileAttributes.Hidden) || fileSystemInfo.Name.StartsWith(".", StringComparison.Ordinal);
    }

    public string GetLocationName(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return path;
        }

        var name = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(name) ? normalized : name;
    }

    public List<FileOpsPathSegment> BuildBreadcrumbs(FileOpsResolvedPath directory)
    {
        var segments = new List<FileOpsPathSegment>
        {
            new()
            {
                Name = GetLocationName(directory.RootPath),
                Path = directory.RootPath
            }
        };

        var relativePath = Path.GetRelativePath(directory.RootPath, directory.FullPath);
        if (relativePath == "." || string.IsNullOrWhiteSpace(relativePath))
        {
            return segments;
        }

        var current = directory.RootPath;
        foreach (var segment in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.GetFullPath(Path.Combine(current, segment));
            segments.Add(new FileOpsPathSegment
            {
                Name = segment,
                Path = current
            });
        }

        return segments;
    }

    private static string ResolveFullPath(string? candidatePath, string fallbackPath)
    {
        var rawPath = string.IsNullOrWhiteSpace(candidatePath) ? fallbackPath : candidatePath;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw FileOpsOperationException.PathRequired();
        }

        return Path.GetFullPath(rawPath.Trim());
    }

    private static string MatchRoot(FileOpsRuntimeConfig runtimeConfig, string fullPath)
    {
        var matchedRoot = runtimeConfig.AllowedRoots
            .Where(rootPath => IsWithinRoot(fullPath, rootPath))
            .OrderByDescending(static rootPath => rootPath.Length)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(matchedRoot))
        {
            throw FileOpsOperationException.PathOutsideAllowedRoots(fullPath);
        }

        return matchedRoot;
    }

    private static void EnsureWithinRoot(string rootPath, string fullPath)
    {
        if (!IsWithinRoot(fullPath, rootPath))
        {
            throw FileOpsOperationException.PathOutsideAllowedRoots(fullPath);
        }
    }

    private static bool IsWithinRoot(string path, string rootPath)
    {
        return FileOpsRuntimeConfig.IsWithinRoot(Path.GetFullPath(path), Path.GetFullPath(rootPath));
    }

    private void EnsureVisible(FileOpsRuntimeConfig runtimeConfig, string fullPath, string rootPath, bool targetMayNotExist = false)
    {
        if (runtimeConfig.AllowHiddenEntries)
        {
            return;
        }

        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (relativePath != "." && !string.IsNullOrWhiteSpace(relativePath))
        {
            var hiddenSegment = relativePath
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(segment => segment.StartsWith(".", StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(hiddenSegment))
            {
                throw FileOpsOperationException.HiddenEntriesDisabled(fullPath);
            }
        }

        if (targetMayNotExist && !File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            var directoryInfo = new DirectoryInfo(fullPath);
            if (IsHidden(directoryInfo))
            {
                throw FileOpsOperationException.HiddenEntriesDisabled(fullPath);
            }

            return;
        }

        if (File.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            if (IsHidden(fileInfo))
            {
                throw FileOpsOperationException.HiddenEntriesDisabled(fullPath);
            }
        }
    }
}
