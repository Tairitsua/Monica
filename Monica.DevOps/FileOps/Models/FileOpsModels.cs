using Monica.Tool.Text;

namespace Monica.DevOps.FileOps.Models;

public enum FileOpsEntryKind
{
    Directory,
    File
}

/// <summary>Observed eligibility of a file for the bounded text preview.</summary>
public enum FileOpsTextPreviewKind
{
    /// <summary>No text observation was taken, including entries supplied by an older provider.</summary>
    Unknown,
    /// <summary>The sampled prefix supports text preview and the file is within the preview limit.</summary>
    Text,
    /// <summary>The sampled prefix contains binary data.</summary>
    Binary,
    /// <summary>The file appears to be text and exceeds the configured text limit.</summary>
    Oversized,
    /// <summary>The prefix could not be read; the exact inspector can report the current cause.</summary>
    Unavailable
}

public class FileOpsPathSegment
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

public class FileOpsEntrySummary
{
    public string Name { get; set; } = string.Empty;

    public string FullPath { get; set; } = string.Empty;

    public FileOpsEntryKind Kind { get; set; }

    public string Extension { get; set; } = string.Empty;

    public long? SizeBytes { get; set; }

    public DateTimeOffset LastModifiedAt { get; set; }

    public bool IsHidden { get; set; }

    public bool IsReadOnly { get; set; }

    public bool CanDownload { get; set; }

    public bool CanEditText { get; set; }

    /// <summary>Gets or sets the preview observation from the bounded directory read.</summary>
    public FileOpsTextPreviewKind TextPreview { get; set; }

    public bool CanDelete { get; set; }

    public bool CanWriteInto { get; set; }

    public bool IsDirectory => Kind == FileOpsEntryKind.Directory;

    public string SizeDisplay => SizeBytes.HasValue ? SizeBytes.Value.FormatByteSize() : "-";
}

public class FileOpsBrowseResult
{
    public string CurrentPath { get; set; } = string.Empty;

    public string CurrentRootPath { get; set; } = string.Empty;

    public string? ParentPath { get; set; }

    public List<FileOpsPathSegment> Breadcrumbs { get; set; } = [];

    public List<FileOpsEntrySummary> Entries { get; set; } = [];

    public bool CanCreateDirectory { get; set; }

    public bool CanUpload { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsTruncated { get; set; }

    public int EntryLimit { get; set; }
}

public class FileOpsFileTextContent
{
    public string FullPath { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string EncodingName { get; set; } = "utf-8";

    public long SizeBytes { get; set; }

    public DateTimeOffset LastModifiedAt { get; set; }

    public int LineCount { get; set; }

    public bool CanSave { get; set; }

    public bool CanDownload { get; set; }

    public bool IsReadOnly { get; set; }
}

public class FileOpsTextUpdateRequest
{
    public string Path { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string EncodingName { get; set; } = "utf-8";
}

public class FileOpsCreateDirectoryRequest
{
    public string ParentPath { get; set; } = string.Empty;

    public string DirectoryName { get; set; } = string.Empty;
}

public class FileOpsDeleteRequest
{
    public string Path { get; set; } = string.Empty;

    public bool Recursive { get; set; }
}

public class FileOpsBatchDeleteRequest
{
    public List<string> Paths { get; set; } = [];

    public bool Recursive { get; set; } = true;
}

public class FileOpsArchiveDownloadRequest
{
    public List<string> Paths { get; set; } = [];

    public string ArchiveName { get; set; } = string.Empty;
}

public sealed record FileOpsUploadPayload(string FileName, long Length, Stream Content);

public class FileOpsUploadResult
{
    public string DirectoryPath { get; set; } = string.Empty;

    public bool Overwrite { get; set; }

    public List<FileOpsEntrySummary> UploadedEntries { get; set; } = [];
}

public class FileOpsDeleteResult
{
    public string Path { get; set; } = string.Empty;

    public FileOpsEntryKind EntryKind { get; set; }
}

public class FileOpsBatchDeleteResult
{
    public List<FileOpsDeleteResult> DeletedEntries { get; set; } = [];
}

public class FileOpsDownloadDescriptor
{
    public string FullPath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public DateTimeOffset LastModifiedAt { get; set; }

    public Stream ContentStream { get; set; } = Stream.Null;
}
