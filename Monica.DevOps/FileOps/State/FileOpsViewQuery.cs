using Microsoft.AspNetCore.WebUtilities;
using Monica.DevOps.FileOps.Pages;

namespace Monica.DevOps.FileOps.State;

/// <summary>Directory inventory categories used in the Files workbench URL.</summary>
public enum FileOpsCapability { All, Dir, Editable, Preview, Binary, Blocked }

/// <summary>Normalized view state for file inventory navigation and deep links.</summary>
public sealed record FileOpsViewQuery(
    string? Path = null, string? File = null, string Search = "",
    FileOpsCapability Capability = FileOpsCapability.All, FileOpsSortField Sort = FileOpsSortField.Name,
    bool Descending = false, bool LegacyPolicy = false)
{
    internal static FileOpsViewQuery Read(Uri uri)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        string? Get(string name) => query.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
        return new(Get("path"), Get("file"), Get("q") ?? "",
            Get("cap") switch
            {
                "dir" => FileOpsCapability.Dir,
                "editable" => FileOpsCapability.Editable,
                "preview" => FileOpsCapability.Preview,
                "binary" => FileOpsCapability.Binary,
                "blocked" => FileOpsCapability.Blocked,
                _ => FileOpsCapability.All
            },
            Get("sort") switch
            {
                "Type" => FileOpsSortField.Type,
                "Size" => FileOpsSortField.Size,
                "Modified" => FileOpsSortField.Modified,
                _ => FileOpsSortField.Name
            },
            Get("dir") == "desc", Get("policy") == "edit");
    }

    internal string Href() => QueryHelpers.AddQueryString(UIFileOpsPage.PAGE_URL, new Dictionary<string, string?>
    {
        ["path"] = Path,
        ["file"] = File,
        ["q"] = Search.Length == 0 ? null : Search,
        ["cap"] = Capability == FileOpsCapability.All ? null : Capability.ToString().ToLowerInvariant(),
        ["sort"] = Sort == FileOpsSortField.Name ? null : Sort.ToString(),
        ["dir"] = Descending ? "desc" : null
    });
}
