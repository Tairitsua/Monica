using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Monica.Markdown.Localization;
using Monica.Markdown.UIMarkdown.Models;
using Monica.Markdown.UIMarkdown.State;

namespace Monica.Markdown.Pages;

/// <summary>Displays the registered Markdown libraries in a shell-bounded reading workspace.</summary>
public partial class UIMarkdownPage : IAsyncDisposable
{
    /// <summary>The reader route; query parameters retain library, logical document path, and culture.</summary>
    public const string PAGE_URL = MarkdownViewerLocation.PAGE_URL;
    [Inject] private MarkdownViewerPageStateFactory PageStateFactory { get; set; } = null!;
    [Inject] private IStringLocalizer<MarkdownResource> L { get; set; } = null!;
    private ElementReference _viewerRoot;
    private MarkdownViewerPageState PageState { get; set; } = null!;
    private int _disposeRequested;
    private const string LayoutClass = "doc-layout mo-page-shell mo-page-shell--compact";
    private string WorkspaceClass => $"doc-workspace{(PageState.ShowSidebar && !PageState.IsSidebarTemporary ? " has-library" : "")}{(PageState.ShowTableOfContents && !PageState.IsOutlineTemporary ? " has-outline" : "")}";
    protected override void OnInitialized() => PageState = PageStateFactory.Create(RequestRenderAsync, action => InvokeAsync(action));
    protected override Task OnInitializedAsync() => PageState.InitializeAsync();
    protected override Task OnAfterRenderAsync(bool firstRender) => PageState.HandleAfterRenderAsync(_viewerRoot, firstRender);
    /// <summary>Retires the visit and drains its owned asynchronous state and interop.</summary>
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        await PageState.DisposeAsync();
    }
    private Task RequestRenderAsync() => Volatile.Read(ref _disposeRequested) != 0 ? Task.CompletedTask : InvokeAsync(StateHasChanged);
    private void HandlePaneKeyDown(KeyboardEventArgs args)
    {
        if (args.Key != "Escape") { return; }
        if (PageState.IsSidebarTemporary) { PageState.SetSidebarOpen(false); }
        if (PageState.IsOutlineTemporary) { PageState.SetOutlineOpen(false); }
    }
}
