using Microsoft.AspNetCore.Components;
using Monica.Core.Results;
using Monica.Framework.UI.UILogging.Models;
using Monica.Framework.UI.UILogging.Components;
using Monica.Framework.UI.UILogging.Support;

namespace Monica.Framework.UI.Pages;

public partial class UILoggingFilesPage
{
    public const string PAGE_URL = "/logging-monitor/files";
    private const int PageSize = 12;

    [SupplyParameterFromQuery(Name = "file")]
    public string? File { get; set; }

    [SupplyParameterFromQuery(Name="q")] public string? Search { get; set; }
    [SupplyParameterFromQuery(Name="folder")] public string? Folder { get; set; }
    [SupplyParameterFromQuery(Name="sort")] public string? Sort { get; set; }
    [SupplyParameterFromQuery(Name="page")] public string? Page { get; set; }
    private int RequestedPage => int.TryParse(Page, out var value) && value > 1 ? value - 1 : 0;
    private string? FolderKey => Folder switch { "all" => null, "." or "/" => "", _ => Folder };
    private ScreenLogSnapshot? _reader;
    private long _total;
    private static readonly (string,string)[] Orders = [("modified","Design:FilesOrderModified"),("size","Design:FilesOrderSize"),("name","Design:FilesOrderName")];
    protected override void OnParametersSet() { _keyword=Search; _order=Sort ?? "modified"; _page=Math.Clamp(RequestedPage,0,PageCount-1); }
    private void SetFolder(string? folder) { _page=0; NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(new Dictionary<string, object?> { ["folder"] = folder=="" ? "." : folder, ["page"] = null }),replace:true); }
    private void SetOrder(string order) { _page=0; NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(new Dictionary<string, object?> { ["sort"] = order, ["page"] = null }),replace:true); }
    private readonly object _ownership = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly HashSet<Task> _operations = [];
    private LogBrowserTransfer? _transfer;
    private Task? _disposeTask;
    private IReadOnlyList<LogFileDescriptor> _files = [];
    private string? _keyword;
    private string? _error;
    private string _order = "modified";
    private DateTimeOffset? _readAt;
    private int _page;
    private bool _busy;
    private volatile bool _disposed;

    private string Query => string.IsNullOrEmpty(File) ? string.Empty : "?file=" + Uri.EscapeDataString(File);
    private string ReaderUrl => NavigationManager.ToAbsoluteUri(UILoggingMonitorPage.PAGE_URL.TrimStart('/') + Query).ToString();
    private string CatalogUrl => NavigationManager.ToAbsoluteUri(PAGE_URL.TrimStart('/') + Query).ToString();
    private string OpenUrl(string path) => NavigationManager.ToAbsoluteUri(UILoggingMonitorPage.PAGE_URL.TrimStart('/') + "?file=" + Uri.EscapeDataString(path)).ToString();
    private IReadOnlyList<LogFileDescriptor> MatchingFiles
    {
        get
        {
            var files = _files.Where(f => string.IsNullOrWhiteSpace(_keyword) || f.RelativePath.Contains(_keyword, StringComparison.OrdinalIgnoreCase)).Where(f=>FolderKey is null || LogFootprint.FolderOf(f)==FolderKey);
            return (_order switch
            {
                "name" => files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
                "size" => files.OrderByDescending(f => f.Size).ThenBy(f => f.RelativePath, StringComparer.Ordinal),
                _ => files.OrderByDescending(f => f.LastModified).ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            }).ToArray();
        }
    }
    private int PageCount => Math.Max(1, (MatchingFiles.Count + PageSize - 1) / PageSize);
    private IEnumerable<LogFileDescriptor> PageFiles => MatchingFiles.Skip(_page * PageSize).Take(PageSize);

    protected override Task OnInitializedAsync()
    {
        _reader = Workspace.GetReader().GetSnapshot();
        _transfer = new LogBrowserTransfer(JSRuntime);
        return RefreshAsync();
    }

    private Task Admit(Func<Task> action)
    {
        lock (_ownership)
        {
            if (_disposed || _busy) return Task.CompletedTask;
            _busy = true;
            var task = RunAsync(action);
            _operations.Add(task);
            _ = ForgetAsync(task);
            return task;
        }
    }

    private async Task ForgetAsync(Task task)
    {
        try { await task; }
        catch { /* RunAsync projects operational failures. */ }
        finally { lock (_ownership) _operations.Remove(task); }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { _error = null; await action(); }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _error = error.Message; }
        finally { if (!_disposed) { _busy = false; StateHasChanged(); } }
    }

    private Task RefreshAsync() => Admit(async () =>
    {
        _reader=Workspace.GetReader().GetSnapshot();
        var result = await FileQuery.ListFilesAsync(_retirement.Token);
        if (_disposed) return;
        var error = result.WithoutData();
        var files = result.Data;
        if (result.IsFailure() || files is null) _error = error.Message ?? "The log file catalog was unavailable.";
        else { _files = files; _total=files.Sum(f=>f.Size); _readAt = DateTimeOffset.UtcNow; _page = Math.Clamp(RequestedPage, 0, PageCount - 1); }
    });

    private Task DownloadAsync(string path) => Admit(() => _transfer!.DownloadAsync(NavigationManager.ToAbsoluteUri(LogBrowserTransfer.FileUrl(path)).ToString()));
    private void SearchChanged(ChangeEventArgs args) { _page = 0; NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameters(new Dictionary<string, object?> { ["q"] = args.Value?.ToString(), ["page"] = null }),replace:true); }
    private void PreviousPage() { if (_page > 0) SetPage(_page - 1); }
    private void NextPage() { if (_page + 1 < PageCount) SetPage(_page + 1); }

    private void SetPage(int index) => NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("page",index + 1),replace:true);

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} B";
        var value = bytes / 1024d;
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    public ValueTask DisposeAsync()
    {
        lock (_ownership)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= RetireAsync());
        }
    }

    private async Task RetireAsync()
    {
        await _retirement.CancelAsync();
        if (_transfer is not null) await _transfer.DisposeAsync();
        Task[] admitted;
        lock (_ownership) admitted = _operations.ToArray();
        try { await Task.WhenAll(admitted); }
        catch (OperationCanceledException) when (_retirement.IsCancellationRequested) { }
        _retirement.Dispose();
    }
}
