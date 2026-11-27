using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.State;

internal static class FileOpsWorkspaceSnapshots
{
    internal static FileOpsEntrySummary Copy(FileOpsEntrySummary value) => new()
    {
        Name = value.Name, FullPath = value.FullPath, Kind = value.Kind, Extension = value.Extension,
        SizeBytes = value.SizeBytes, LastModifiedAt = value.LastModifiedAt, IsHidden = value.IsHidden,
        IsReadOnly = value.IsReadOnly, CanDownload = value.CanDownload, CanEditText = value.CanEditText, TextPreview = value.TextPreview,
        CanDelete = value.CanDelete, CanWriteInto = value.CanWriteInto
    };

    internal static FileOpsBrowseResult Copy(FileOpsBrowseResult value) => new()
    {
        CurrentPath = value.CurrentPath, CurrentRootPath = value.CurrentRootPath, ParentPath = value.ParentPath,
        Breadcrumbs = value.Breadcrumbs.Select(segment => new FileOpsPathSegment { Name = segment.Name, Path = segment.Path }).ToList(),
        Entries = value.Entries.Select(Copy).ToList(), CanCreateDirectory = value.CanCreateDirectory,
        CanUpload = value.CanUpload, IsReadOnly = value.IsReadOnly, IsTruncated = value.IsTruncated, EntryLimit = value.EntryLimit
    };

    internal static FileOpsFileTextContent Copy(FileOpsFileTextContent value) => new()
    {
        FullPath = value.FullPath, Name = value.Name, Extension = value.Extension, Content = value.Content,
        EncodingName = value.EncodingName, SizeBytes = value.SizeBytes, LastModifiedAt = value.LastModifiedAt,
        LineCount = value.LineCount, CanSave = value.CanSave, CanDownload = value.CanDownload, IsReadOnly = value.IsReadOnly
    };
}
