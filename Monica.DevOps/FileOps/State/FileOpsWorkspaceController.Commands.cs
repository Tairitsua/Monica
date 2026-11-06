using System.Runtime.ExceptionServices;
using Monica.Core.Results;
using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.State;

public sealed partial class FileOpsWorkspaceController
{
    /// <summary>Creates a directory under the parent frozen before native name collection.</summary>
    public Task<FileOpsOperationDisposition> CreateDirectoryAsync(FileOpsDirectoryNameCollector collect) => Admit(() =>
    {
        var origin = _browse?.CurrentPath;
        return async () =>
        {
            if (origin is null) return FileOpsOperationDisposition.Rejected;
            var name = await collect(new(origin), _token);
            if (name is null || IsRetired) return DispositionOrCancel;
            var result = await _facade.CreateDirectoryAsync(new() { ParentPath = origin, DirectoryName = name.Trim() }, CancellationToken.None);
            LastCommand = FileOpsOutcome.From(FileOpsOperationKind.CreateDirectory, result);
            await RefreshCoreAsync(origin);
            return Disposition;
        };
    });

    /// <summary>Deletes one frozen entry after native confirmation including any affected dirty draft.</summary>
    public Task<FileOpsOperationDisposition> DeleteEntryAsync(FileOpsEntrySummary entry, FileOpsDeleteConfirmationCallback confirm) =>
        DeleteAsync(() => [FileOpsWorkspaceSnapshots.Copy(entry)], batch: false, confirm);

    /// <summary>Deletes the frozen displayed checked subset sequentially through the real facade.</summary>
    /// <remarks>Failure has no precise partial count. Refresh still observes the captured directory because
    /// earlier deletions may already have applied; unrelated inspection and dirty text remain separate.</remarks>
    public Task<FileOpsOperationDisposition> DeleteSelectedAsync(FileOpsDeleteConfirmationCallback confirm) =>
        DeleteAsync(() => _browse?.Entries.Where(entry => _selected.Contains(entry.FullPath)).Select(FileOpsWorkspaceSnapshots.Copy).ToArray() ?? [], batch: true, confirm);

    private Task<FileOpsOperationDisposition> DeleteAsync(Func<FileOpsEntrySummary[]> captureEntries, bool batch, FileOpsDeleteConfirmationCallback confirm) => Admit(() =>
    {
        var entries = captureEntries();
        var origin = _browse?.CurrentPath;
        var stamp = CaptureDraft();
        var affects = stamp.Path is not null && entries.Any(entry => Affects(entry, stamp.Path));
        return async () =>
        {
            if (entries.Length == 0) return FileOpsOperationDisposition.Rejected;
            var recursive = await confirm(new(entries.Select(FileOpsWorkspaceSnapshots.Copy).ToArray(), affects && stamp.Dirty, affects ? stamp.Path : null), _token);
            if (recursive is null || IsRetired || affects && !Matches(stamp)) return DispositionOrCancel;
            if (batch)
            {
                var result = await _facade.DeleteEntriesAsync(new() { Paths = entries.Select(entry => entry.FullPath).ToList(), Recursive = recursive.Value }, CancellationToken.None);
                LastCommand = FileOpsOutcome.From(FileOpsOperationKind.DeleteSelection, result, result.Data is not null);
            }
            else
            {
                var result = await _facade.DeleteEntryAsync(new() { Path = entries[0].FullPath, Recursive = recursive.Value }, CancellationToken.None);
                LastCommand = FileOpsOutcome.From(FileOpsOperationKind.DeleteEntry, result);
            }
            if (LastCommand.Succeeded && affects && Matches(stamp)) ClearInspection();
            else if (affects) MetadataStale = true;
            await RefreshCoreAsync(origin);
            return Disposition;
        };
    });

    /// <summary>Persists captured text once and directly rereads the same target within the admitted scope.</summary>
    /// <remarks>Persistence success is independent of reread success. A newer user edit survives; retained
    /// metadata is marked stale when reread fails. Real disk changes are evidence, not optimistic locking.</remarks>
    public Task<FileOpsOperationDisposition> SaveAsync() => Admit(() =>
    {
        var file = _file;
        var stamp = CaptureDraft();
        var origin = _browse?.CurrentPath;
        var request = file is null ? null : new FileOpsTextUpdateRequest { Path = file.FullPath, Content = _draft, EncodingName = file.EncodingName };
        return async () =>
        {
            if (request is null) return FileOpsOperationDisposition.Rejected;
            var result = await _facade.SaveTextFileAsync(request, CancellationToken.None);
            LastCommand = FileOpsOutcome.From(FileOpsOperationKind.SaveText, result);
            if (LastCommand.Succeeded)
            {
                // Reconcile the applied content even during retirement; retained metadata remains real.
                if (_inspectionId == stamp.InspectionId && _file is not null && PathsEqual(_file.FullPath, request.Path))
                {
                    _baseline = request.Content;
                    MetadataStale = true;
                }
                if (!IsRetired) await ReadInspectionAsync(request.Path, replace: false, stamp, keepSavedBaseline: true);
                if (!IsRetired) await ReadPolicyAsync();
                if (!IsRetired) await BrowseCoreAsync(origin);
            }
            else await RefreshCoreAsync(origin);
            return Disposition;
        };
    });

    /// <summary>Uploads one captured browser file with overwrite enabled and controller-owned input disposal.</summary>
    /// <remarks>Capture the browser-file object in openStream's closure. The controller freezes its supplied
    /// name, size, parent and maximum before confirmation, then owns the actual returned stream. A started
    /// upload is uncancelled; partial/truncated content on failure is not rolled back.</remarks>
    public Task<FileOpsOperationDisposition> UploadAsync(string name, long size,
        Func<long, CancellationToken, Task<Stream>> openStream, FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var origin = _browse?.CurrentPath;
        var maximum = _config?.MaxUploadFileSizeBytes;
        var stamp = CaptureDraft();
        // ResolveChildPath trims the child name; confirmation and upload must share that identity.
        var childName = name.Trim();
        var target = origin is null ? null : Path.GetFullPath(Path.Combine(origin, childName));
        var affects = stamp.Path is not null && target is not null && PathsEqual(stamp.Path, target);
        return async () =>
        {
            if (origin is null || maximum is null) return FileOpsOperationDisposition.Rejected;
            if (affects && !await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.UploadReplacement, target, discard)) return DispositionOrCancel;
            if (IsRetired) return FileOpsOperationDisposition.Retired;
            var content = await openStream(maximum.Value, CancellationToken.None);
            return await UseStreamAsync(content, async () =>
            {
                if (IsRetired) return FileOpsOperationDisposition.Retired;
                if (affects && !Matches(stamp)) return FileOpsOperationDisposition.Cancelled;
                var result = await _facade.UploadAsync(origin, [new(childName, size, content)], overwrite: true, CancellationToken.None);
                LastCommand = FileOpsOutcome.From(FileOpsOperationKind.Upload, result, result.Data is not null);
                if (LastCommand.Succeeded && affects && Matches(stamp))
                {
                    // Only a successful real read can replace inspection with uploaded bytes.
                    MetadataStale = true;
                    if (!IsRetired) await ReadInspectionAsync(stamp.Path!, replace: true, stamp, _entry);
                }
                await RefreshCoreAsync(origin);
                return Disposition;
            });
        };
    });

    /// <summary>Opens one real owned descriptor and lends it to a drained browser transfer callback.</summary>
    public Task<FileOpsOperationDisposition> DownloadAsync(string path, FileOpsDownloadCallback transfer) => Admit(() =>
    {
        var target = path;
        return () => TransferAsync(() => _facade.OpenDownloadAsync(target, _token), FileOpsOperationKind.Download, transfer);
    });

    /// <summary>Archives the frozen checked directories/downloadable files and drains browser initiation.</summary>
    /// <remarks>Archive recursion is governed by the actual service, not the displayed directory cap.
    /// This operation supplies no total archive-size limit or completed-browser-persistence acknowledgment.</remarks>
    public Task<FileOpsOperationDisposition> DownloadSelectedAsync(string archiveName, FileOpsDownloadCallback transfer) => Admit(() =>
    {
        var paths = _browse?.Entries.Where(entry => _selected.Contains(entry.FullPath) && (entry.IsDirectory || entry.CanDownload))
            .Select(entry => entry.FullPath).ToList() ?? [];
        var request = new FileOpsArchiveDownloadRequest { Paths = paths, ArchiveName = archiveName };
        return () => paths.Count == 0 ? Task.FromResult(FileOpsOperationDisposition.Rejected)
            : TransferAsync(() => _facade.OpenArchiveDownloadAsync(request, _token), FileOpsOperationKind.DownloadSelection, transfer);
    });

    private async Task<FileOpsOperationDisposition> TransferAsync(
        Func<Task<Result<FileOpsDownloadDescriptor>>> open, FileOpsOperationKind kind, FileOpsDownloadCallback transfer)
    {
        if (IsRetired) return FileOpsOperationDisposition.Retired;
        var result = await open();
        // Disposal owns even a descriptor produced by a cancellation-ignoring service after retirement.
        var descriptor = result.Data;
        if (descriptor is null)
        {
            if (!IsRetired) LastCommand = FileOpsOutcome.From(kind, result, hasData: false);
            return Disposition;
        }
        return await UseStreamAsync(descriptor.ContentStream, async () =>
        {
            if (IsRetired) return FileOpsOperationDisposition.Retired;
            var outcome = FileOpsOutcome.From(kind, result);
            if (!outcome.Succeeded) { LastCommand = outcome; return Disposition; }
            LastCommand = null;
            var initiated = await transfer(descriptor, _token);
            if (IsRetired) return FileOpsOperationDisposition.Retired;
            if (!initiated) return FileOpsOperationDisposition.Cancelled;
            LastCommand = outcome;
            return Disposition;
        });
    }

    private static async Task<T> UseStreamAsync<T>(Stream stream, Func<Task<T>> use)
    {
        T result = default!;
        Exception? failure = null;
        try { result = await use(); } catch (Exception exception) { failure = exception; }
        try { await stream.DisposeAsync(); }
        catch (Exception exception) { failure = failure is null ? exception : new AggregateException(failure, exception); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    /// <summary>Replaces captured normalized host policy, then reads authoritative policy and reconciles scope.</summary>
    /// <remarks>Empty roots retain the existing service's broad normalization defaults. The frontend owns
    /// validation/presentation; cancellation or failed persistence never discards a current draft.</remarks>
    public Task<FileOpsOperationDisposition> SavePolicyAsync(FileOpsRuntimeConfig draft, FileOpsDiscardConfirmation? discard = null) => Admit(() =>
    {
        var config = draft.Clone();
        var origin = _browse?.CurrentPath;
        var stamp = CaptureDraft();
        var file = _file is null ? null : FileOpsWorkspaceSnapshots.Copy(_file);
        var hidden = _entry?.IsHidden == true;
        return async () =>
        {
            // Normalization probes the startup directory; perform it after releasing admission's lock.
            config.Normalize();
            var affects = file is not null && Invalidates(config, file, hidden);
            if (affects && !await ConfirmDiscardAsync(stamp, FileOpsDiscardReason.PolicyReplacement, config.StartupPath, discard)) return DispositionOrCancel;
            if (IsRetired) return FileOpsOperationDisposition.Retired;
            var result = await _facade.SaveRuntimeConfigAsync(config, CancellationToken.None);
            LastCommand = FileOpsOutcome.From(FileOpsOperationKind.SavePolicy, result);
            if (!LastCommand.Succeeded) { await RefreshCoreAsync(origin); return Disposition; }
            if (!await ReadPolicyAsync() || IsRetired) return Disposition;
            var draftUnchanged = Matches(stamp);
            if (affects && draftUnchanged) ClearInspection();
            else if (affects) MetadataStale = true;
            if (origin is not null)
            {
                var target = _config!.AllowedRoots.Any(root => IsWithin(origin, root)) ? origin : _config.StartupPath;
                // An older policy/discard scope cannot relocate a newer draft after policy readback.
                if (!draftUnchanged && !PathsEqual(origin, target)) { BrowseStale = true; return Disposition; }
                await BrowseCoreAsync(target);
                await RefreshInspectionAsync();
            }
            return Disposition;
        };
    });

    private static bool Affects(FileOpsEntrySummary entry, string path) =>
        PathsEqual(entry.FullPath, path) || entry.IsDirectory && IsWithin(path, entry.FullPath);

    private static bool Invalidates(FileOpsRuntimeConfig config, FileOpsFileTextContent file, bool isHidden)
    {
        var root = config.AllowedRoots.Where(root => IsWithin(file.FullPath, root)).OrderByDescending(root => root.Length).FirstOrDefault();
        if (root is null) return true;
        // Hidden lexical segments are relative to the longest allowed root, as in the real policy.
        var relative = Path.GetRelativePath(root, file.FullPath);
        var hidden = isHidden || relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith('.'));
        return config.ReadOnlyPaths.Any(path => IsWithin(file.FullPath, path)) || !config.AllowWriteOperations ||
            !config.CanEditExtension(file.Extension) || file.SizeBytes > config.MaxTextFileSizeBytes || !config.AllowHiddenEntries && hidden;
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return PathsEqual(normalizedPath, normalizedRoot) || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
