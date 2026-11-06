using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Models;
using Monica.Logging.Services.Support;
using Monica.Modules;

namespace Monica.Framework.UI.UILogging.Support;

/// <summary>Shared stateless catalog, context and download access beneath the configured local log directory.</summary>
public sealed class LogFileQueryService(
    IOptions<ModuleLoggingOption> loggingOptions,
    IOptions<ModuleLoggingUIOption> uiOptions,
    ILogger<LogFileQueryService> logger)
{
    private readonly string _logDirectory = !string.IsNullOrWhiteSpace(uiOptions.Value.LogDirectory)
        ? Path.GetFullPath(uiOptions.Value.LogDirectory)
        : Path.GetDirectoryName(LogFileLocation.Resolve(loggingOptions.Value))!;

    public string LogDirectory => _logDirectory;

    /// <summary>
    /// Lists ordinary files recursively. A missing or unavailable configured directory returns a failed
    /// result, distinct from an existing empty directory. Symbolic-link descendants are excluded.
    /// </summary>
    public Task<Result<IReadOnlyList<LogFileDescriptor>>> ListFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!Directory.Exists(_logDirectory))
                return Task.FromResult(Result.Failure<IReadOnlyList<LogFileDescriptor>>($"The configured log directory does not exist or cannot be accessed: {_logDirectory}"));
            var files = new List<LogFileDescriptor>();
            foreach (var file in new DirectoryInfo(_logDirectory).EnumerateFiles("*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new LogFileDescriptor(file.Name, file.Length, file.LastWriteTimeUtc,
                    Path.GetRelativePath(_logDirectory, file.FullName).Replace(Path.DirectorySeparatorChar, '/')));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success<IReadOnlyList<LogFileDescriptor>>(Array.AsReadOnly(files.OrderByDescending(file => file.LastModified)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal).ToArray())));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list log directory {LogDirectory}.", _logDirectory);
            return Task.FromResult(Result.Failure<IReadOnlyList<LogFileDescriptor>>($"Failed to list log directory: {ex.Message}"));
        }
    }

    /// <summary>
    /// Opens a file for sequential asynchronous reading while permitting append and rotation.
    /// The caller owns the returned stream; this is a live handle, not an immutable archive snapshot.
    /// Cancellation throws before a handle is returned. Paths outside the inspection directory are rejected.
    /// </summary>
    public Task<Result<FileStream>> OpenFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? stream = null;
        try
        {
            var fullPath = ResolveFilePath(relativePath);
            if (!File.Exists(fullPath)) return Task.FromResult(Result.Failure<FileStream>($"The requested log file does not exist: {relativePath}"));
            stream = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Success(stream));
        }
        catch (OperationCanceledException) { stream?.Dispose(); throw; }
        catch (Exception ex)
        {
            stream?.Dispose();
            logger.LogWarning(ex, "Failed to open log file {RelativePath}.", relativePath);
            return Task.FromResult(Result.Failure<FileStream>($"Failed to open log file: {ex.Message}"));
        }
    }

    /// <summary>
    /// Compatibility context read beneath this service's directory. This overload has no source-generation
    /// proof; reader pages should use LogReadingSession.ReadContextAsync with their frozen line request.
    /// </summary>
    public async Task<Result<ContextLogResult>> ReadContextAsync(string filePath, long targetLineNumber,
        int contextLines = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var fullPath = ResolveFilePath(Path.GetRelativePath(_logDirectory, Path.GetFullPath(filePath)));
            if (!File.Exists(fullPath)) return Result.Failure<ContextLogResult>($"The log file does not exist: {filePath}");
            if (targetLineNumber < 1 || contextLines < 0) return Result.Failure<ContextLogResult>("Log context requires a positive target line and nonnegative context count.");
            var start = Math.Max(1, targetLineNumber - contextLines);
            var end = targetLineNumber > long.MaxValue - contextLines ? long.MaxValue : targetLineNumber + contextLines;
            var raw = await LogTailService.ReadRangeAsync(fullPath, start, end, cancellationToken).ConfigureAwait(false);
            var lines = raw.Select((line, index) => LogLineViewModel.FromRaw(line).WithPosition(start + index, null)
                .WithContextTarget(start + index == targetLineNumber)).ToArray();
            if (!lines.Any(line => line.IsContextTarget)) return Result.Failure<ContextLogResult>("The requested physical line is no longer available.");
            cancellationToken.ThrowIfCancellationRequested();
            return Result.Success(new ContextLogResult(Array.AsReadOnly(lines), targetLineNumber, start, start + lines.Length - 1) { ReadAt = DateTimeOffset.UtcNow });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read log context at physical line {LineNumber}.", targetLineNumber);
            return Result.Failure<ContextLogResult>($"Failed to read log context: {ex.Message}");
        }
    }

    /// <summary>Resolves a relative ordinary file path beneath the catalog directory.</summary>
    /// <exception cref="InvalidOperationException">The path escapes the directory or traverses a symbolic-link descendant.</exception>
    public string ResolveFilePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath)) throw new InvalidOperationException("Log file access requires a path relative to the inspection directory.");
        var fullPath = Path.GetFullPath(Path.Combine(_logDirectory, relativePath));
        var root = Path.TrimEndingDirectorySeparator(_logDirectory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root, comparison)) throw new InvalidOperationException("The requested path is outside the log inspection directory.");
        var current = _logDirectory;
        foreach (var part in Path.GetRelativePath(_logDirectory, fullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic-link descendants are outside the log inspection boundary.");
        }
        return fullPath;
    }
}
