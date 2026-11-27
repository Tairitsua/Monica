using Microsoft.Extensions.Logging.Abstractions;
using Monica.DevOps.FileOps.Abstractions;
using Monica.DevOps.FileOps.Facades;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.Services.Support;
using Monica.DevOps.Localization;
using Monica.Testing.Localization;

namespace Test.Monica.DevOps.FileOps.Support;

// Scenario-owned filesystem/transfer boundaries; orchestration always uses the production facade.
internal sealed class ControlledFileOpsServices : IFileOpsWorkspaceService, IFileOpsTransferService
{
    public static string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "controlled-file-ops"));

    public Func<CancellationToken, Task<FileOpsRuntimeConfig>> ReadConfig { get; set; } =
        _ => Task.FromResult(new FileOpsRuntimeConfig { AllowedRoots = [Root], StartupPath = Root });
    public Func<string?, CancellationToken, Task<FileOpsBrowseResult>> Browse { get; set; } =
        (path, _) => Task.FromResult(DirectorySnapshot(path ?? Root));
    public Func<string, CancellationToken, Task<FileOpsFileTextContent>> ReadText { get; set; } =
        (path, _) => Task.FromResult(TextSnapshot(path));
    public Func<FileOpsTextUpdateRequest, CancellationToken, Task> SaveText { get; set; } = (_, _) => Task.CompletedTask;
    public Func<FileOpsCreateDirectoryRequest, CancellationToken, Task<string>> CreateDirectory { get; set; } =
        (request, _) => Task.FromResult(Path.Combine(request.ParentPath, request.DirectoryName));
    public Func<FileOpsDeleteRequest, CancellationToken, Task<FileOpsDeleteResult>> DeleteEntry { get; set; } =
        (request, _) => Task.FromResult(new FileOpsDeleteResult { Path = request.Path, EntryKind = FileOpsEntryKind.File });
    public Func<FileOpsBatchDeleteRequest, CancellationToken, Task<FileOpsBatchDeleteResult>> DeleteEntries { get; set; } =
        (request, _) => Task.FromResult(new FileOpsBatchDeleteResult
        {
            DeletedEntries = request.Paths.Select(path => new FileOpsDeleteResult { Path = path, EntryKind = FileOpsEntryKind.File }).ToList()
        });
    public Func<FileOpsRuntimeConfig, CancellationToken, Task> SaveConfig { get; set; } = (_, _) => Task.CompletedTask;
    public Func<string, CancellationToken, Task<FileOpsDownloadDescriptor>> OpenDownload { get; set; } =
        (path, _) => Task.FromResult(Descriptor(path));
    public Func<FileOpsArchiveDownloadRequest, CancellationToken, Task<FileOpsDownloadDescriptor>> OpenArchive { get; set; } =
        (request, _) => Task.FromResult(Descriptor(Path.Combine(Root, request.ArchiveName)));
    public Func<string, string, long, Stream, bool, CancellationToken, Task<FileOpsEntrySummary>> Upload { get; set; } =
        (parent, name, length, _, _, _) => Task.FromResult(Entry(Path.Combine(parent, name), size: length));

    public int ConfigReads { get; private set; }
    public List<(string? Path, CancellationToken Token)> Browses { get; } = [];
    public List<(string Path, CancellationToken Token)> TextReads { get; } = [];
    public List<(FileOpsTextUpdateRequest Request, CancellationToken Token)> TextSaves { get; } = [];
    public List<(FileOpsCreateDirectoryRequest Request, CancellationToken Token)> DirectoryCreates { get; } = [];
    public List<(FileOpsDeleteRequest Request, CancellationToken Token)> SingleDeletes { get; } = [];
    public List<(FileOpsBatchDeleteRequest Request, CancellationToken Token)> BatchDeletes { get; } = [];
    public List<(FileOpsRuntimeConfig Config, CancellationToken Token)> ConfigSaves { get; } = [];
    public List<(string Path, CancellationToken Token)> Downloads { get; } = [];
    public List<(FileOpsArchiveDownloadRequest Request, CancellationToken Token)> Archives { get; } = [];
    public List<(string Parent, string Name, bool Overwrite, CancellationToken Token)> Uploads { get; } = [];

    public FileOpsFacade CreateFacade()
    {
        var localizer = new EchoStringLocalizer<FileOpsResource>();
        return new FileOpsFacade(this, this, new FileOpsMessageLocalizer(localizer), localizer,
            NullLogger<FileOpsFacade>.Instance);
    }

    public Task<FileOpsRuntimeConfig> GetRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        ConfigReads++;
        return ReadConfig(cancellationToken);
    }

    public Task UpdateRuntimeConfigAsync(FileOpsRuntimeConfig runtimeConfig, CancellationToken cancellationToken = default)
    {
        ConfigSaves.Add((runtimeConfig, cancellationToken));
        return SaveConfig(runtimeConfig, cancellationToken);
    }

    public Task<FileOpsBrowseResult> BrowseAsync(string? path, CancellationToken cancellationToken = default)
    {
        Browses.Add((path, cancellationToken));
        return Browse(path, cancellationToken);
    }

    public Task<FileOpsEntrySummary> ObserveFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromException<FileOpsEntrySummary>(new FileNotFoundException());

    public Task<FileOpsFileTextContent> ReadTextFileAsync(string path, CancellationToken cancellationToken = default)
    {
        TextReads.Add((path, cancellationToken));
        return ReadText(path, cancellationToken);
    }

    public Task SaveTextFileAsync(FileOpsTextUpdateRequest request, CancellationToken cancellationToken = default)
    {
        TextSaves.Add((request, cancellationToken));
        return SaveText(request, cancellationToken);
    }

    public Task<string> CreateDirectoryAsync(FileOpsCreateDirectoryRequest request, CancellationToken cancellationToken = default)
    {
        DirectoryCreates.Add((request, cancellationToken));
        return CreateDirectory(request, cancellationToken);
    }

    public Task<FileOpsDeleteResult> DeleteEntryAsync(FileOpsDeleteRequest request, CancellationToken cancellationToken = default)
    {
        SingleDeletes.Add((request, cancellationToken));
        return DeleteEntry(request, cancellationToken);
    }

    public Task<FileOpsBatchDeleteResult> DeleteEntriesAsync(FileOpsBatchDeleteRequest request, CancellationToken cancellationToken = default)
    {
        BatchDeletes.Add((request, cancellationToken));
        return DeleteEntries(request, cancellationToken);
    }

    public Task<FileOpsEntrySummary> SaveUploadAsync(string directoryPath, string fileName, long contentLength,
        Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        Uploads.Add((directoryPath, fileName, overwrite, cancellationToken));
        return Upload(directoryPath, fileName, contentLength, content, overwrite, cancellationToken);
    }

    public Task<FileOpsDownloadDescriptor> OpenDownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        Downloads.Add((path, cancellationToken));
        return OpenDownload(path, cancellationToken);
    }

    public Task<FileOpsDownloadDescriptor> OpenArchiveDownloadAsync(FileOpsArchiveDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        Archives.Add((request, cancellationToken));
        return OpenArchive(request, cancellationToken);
    }

    public static FileOpsBrowseResult DirectorySnapshot(string path, params FileOpsEntrySummary[] entries) => new()
    {
        CurrentPath = path, CurrentRootPath = Root, CanCreateDirectory = true, CanUpload = true,
        Entries = entries.Length == 0 ? [Entry(Path.Combine(path, "alpha.txt")), Entry(Path.Combine(path, "beta.txt"))] : [.. entries],
        EntryLimit = 500
    };

    public static FileOpsEntrySummary Entry(string path, bool directory = false, long size = 10) => new()
    {
        Name = Path.GetFileName(path), FullPath = path, Kind = directory ? FileOpsEntryKind.Directory : FileOpsEntryKind.File,
        Extension = directory ? string.Empty : Path.GetExtension(path), SizeBytes = directory ? null : size,
        CanDelete = true, CanDownload = !directory, CanEditText = !directory, CanWriteInto = directory,
        LastModifiedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
    };

    public static FileOpsFileTextContent TextSnapshot(string path, string content = "accepted text", long size = 13,
        int lineCount = 1) => new()
    {
        FullPath = path, Name = Path.GetFileName(path), Extension = Path.GetExtension(path), Content = content,
        EncodingName = "utf-8", SizeBytes = size, LineCount = lineCount, CanSave = true, CanDownload = true,
        LastModifiedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)
    };

    public static FileOpsDownloadDescriptor Descriptor(string path, Stream? stream = null) => new()
    {
        FullPath = path, FileName = Path.GetFileName(path), ContentStream = stream ?? new TrackingDownloadStream(), SizeBytes = 3
    };
}

internal sealed class TrackingDownloadStream() : MemoryStream([1, 2, 3])
{
    public int DisposeCount { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeCount++;
        base.Dispose(disposing);
    }
}

internal sealed class FailingDownloadStream(Exception releaseFailure) : MemoryStream([1, 2, 3])
{
    public int DisposeCount { get; private set; }

    public override ValueTask DisposeAsync()
    {
        DisposeCount++;
        base.Dispose();
        return ValueTask.FromException(releaseFailure);
    }
}
