# Serve and search Markdown documents

`AddMarkdown()` registers the document catalog, search, and `MarkdownFacade`; its default provider scans the filesystem. Give it an explicit discovery root with `AddDocumentGroup(key, title, basePath)`. `AddMarkdownUI()` adds the viewer page, shell Markdown rendering, localization, and a local image endpoint. The viewer needs a Web host completed with `app.UseMonica()` and `app.MapMonica()`.

```csharp
builder.AddMonica(monica =>
{
    monica.AddMarkdown()
        .AddDocumentGroup("handbook", "Handbook", "docs/handbook")
        .EnableMultilingualDocuments();
    monica.AddMarkdownUI();
});
```

The catalog recognizes `.md` and `.markdown` by default, parses front matter for titles and navigation metadata, and excludes common tool/build folders. `AddExcludedFolders(...)` extends those exclusions; `WithExcludedFolders(...)` replaces them. `EnableMultilingualDocuments()` treats top-level supported culture folders such as `en-US` and `zh-CN` as language roots; when such roots are detected, documents should live below a supported root. `AddDocumentGroup` can also specify per-group exclusions. Use `UseDocumentProvider<TProvider>()` or `UseDocumentTitleProvider<TProvider>()` only when filesystem discovery or filename titles do not fit the host.

Code consumers use `MarkdownFacade`: `GetAllDocumentGroupsAsync()` selects a group, `GetDocumentTreeAsync(groupKey, culture)` gets its hierarchy, `GetDocumentContentAsync(document)` reads source, and `SearchDocumentsAsync(new MarkdownDocumentSearchRequest(query, CurrentGroupKey: groupKey))` searches it. Search defaults to keyword fuzzy matching, a minimum normalized query length of 2, at most 50 results, and 180 preview characters. `RefreshGroupAsync(groupKey)` or `RefreshAllAsync()` rescans after files change. Each method returns `Res` and reports a failed read/search through that envelope; check it before using `Data`.

The viewer's local image asset endpoint is on by default at `/markdown-ui/assets/{groupKey}`. It validates the referenced relative path and allowed extension before serving an image; set `EnableLocalImageAssetEndpoint = false` when the viewer needs no local images. The shell alone can render basic Markdown components when `EnableMarkdown = true`, but it does not provide searchable groups or the document viewer.

Source and checks: `Monica.Markdown/Modules/ModuleMarkdown.cs` and `ModuleMarkdownUI.cs`, `Monica.Markdown/Facades/MarkdownFacade.cs`, `Monica.Markdown/Models/MarkdownDocumentSearchRequest.cs`, and `tests/Test.Monica.Markdown/Modules/ModuleMarkdownUITests.cs`.
