using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Monica.AI.RAG.Models;
using Monica.AI.UI.UIRAG.Components;
using Monica.AI.UI.UIRAG.Support;
using Monica.Core.Results;
using Monica.UI.Shared.Components.Dialogs;
using MudBlazor;

namespace Monica.AI.UI.Pages;

public partial class RAGChunkersPage : IAsyncDisposable
{
    public const string PAGE_URL = "/ai/rag/chunkers";
    private readonly RetrievalWorkbenchLifetime _lifetime = new();
    private IReadOnlyList<ChunkerDescriptor> _chunkers = [];
    private List<RouteEditor> _routes = [];
    private bool _isLoading = true;
    private bool _isTesting;
    private long _stateGeneration;
    private long _focusGeneration;
    private string? _restoreFocusExtension;
    private string? _lastActiveExtension;
    private MudButton? _refreshButton;
    private string _loadError = string.Empty;
    private string _testError = string.Empty;
    private string _testChunkerId = string.Empty;
    private string _testDocumentName = "sample.md";
    private string _testContent = "# Sample\n\nThis is a sample document for chunker testing.";
    private ChunkerTestResult? _testResult;
    private bool TestInputsChanged => _testResult is not null && (_testResult.ChunkerId != _testChunkerId
        || _testResult.DocumentName != EffectiveTestName || _testResult.OriginalText != _testContent);
    private string EffectiveTestName => string.IsNullOrWhiteSpace(_testDocumentName) ? "sample.md" : _testDocumentName;
    private bool MutationBusy => _routes.Any(route => route.IsApplying);
    protected override Task OnInitializedAsync() => _lifetime.RunAsync(LoadStateCoreAsync);
    private Task LoadStateAsync() => _lifetime.RunAsync(async () =>
    {
        if (_isLoading || _isTesting || MutationBusy) return;
        var extension = _lastActiveExtension ?? _routes.FirstOrDefault()?.Extension;
        await LoadStateCoreAsync();
        QueueFocusRestoration(extension);
    });
    private async Task LoadStateCoreAsync()
    {
        var generation = ++_stateGeneration;
        ++_focusGeneration;
        _isLoading = true;
        _loadError = string.Empty;
        _testResult = null;
        _testError = string.Empty;
        StateHasChanged();
        var result = await ChunkerService.GetManagementStateAsync();
        if (_lifetime.Retired || generation != _stateGeneration) return;
        var error = result.WithoutData();
        var state = result.Data;
        if (result.IsFailure() || state is null) _loadError = error.Message ?? L["KnowledgeWorkbench:Chunking:LoadFailed"];
        else
        {
            _chunkers = state.Chunkers;
            _routes = state.Routes.Select(route => new RouteEditor
            {
                Extension = route.Extension, CurrentChunkerId = route.DefaultChunkerId,
                SelectedChunkerId = route.DefaultChunkerId, CandidateChunkerIds = route.CandidateChunkerIds
            }).ToList();
            if (string.IsNullOrWhiteSpace(_testChunkerId) && _chunkers.Count > 0) _testChunkerId = _chunkers[0].ChunkerId;
        }
        _isLoading = false;
    }
    private static bool Changed(RouteEditor route) => !string.Equals(route.CurrentChunkerId, route.SelectedChunkerId, StringComparison.OrdinalIgnoreCase);
    private bool IsCurrent(RouteEditor route, long version, string target) => !_lifetime.Retired && !_isLoading
        && _routes.Contains(route) && route.Version == version
        && string.Equals(route.SelectedChunkerId, target, StringComparison.OrdinalIgnoreCase);
    private string ChunkerName(string id) => _chunkers.FirstOrDefault(chunker => string.Equals(chunker.ChunkerId, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;
    private Task OnRouteTargetChangedAsync(RouteEditor route, string target) => _lifetime.RunAsync(async () =>
    {
        if (_isLoading || MutationBusy || !_routes.Contains(route) || !route.CandidateChunkerIds.Contains(target, StringComparer.OrdinalIgnoreCase)) return;
        _lastActiveExtension = route.Extension;
        route.SelectedChunkerId = target;
        route.Preview = null;
        route.Error = string.Empty;
        var version = ++route.Version;
        route.IsPreviewing = false;
        if (Changed(route)) await PreviewCoreAsync(route, version, target);
    });
    private Task RetryPreviewAsync(RouteEditor route) => _lifetime.RunAsync(async () =>
    {
        if (_isLoading || MutationBusy || route.IsPreviewing || !Changed(route)) return;
        await PreviewCoreAsync(route, ++route.Version, route.SelectedChunkerId);
    });
    private async Task PreviewCoreAsync(RouteEditor route, long version, string target)
    {
        route.IsPreviewing = true;
        route.Preview = null;
        route.Error = string.Empty;
        StateHasChanged();
        var result = await ChunkerService.PreviewRoutingChangeAsync(route.Extension, target);
        if (!IsCurrent(route, version, target)) return;
        route.IsPreviewing = false;
        var error = result.WithoutData();
        var preview = result.Data;
        if (result.IsFailure() || preview is null) route.Error = error.Message ?? L["KnowledgeWorkbench:Chunking:PreviewFailed"];
        else if (string.Equals(preview.Extension, route.Extension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(preview.TargetChunkerId, target, StringComparison.OrdinalIgnoreCase)) route.Preview = preview;
        else route.Error = L["KnowledgeWorkbench:Chunking:PreviewChanged"];
    }
    private Task ApplyRouteChangeAsync(RouteEditor route) => _lifetime.RunAsync(async () =>
    {
        if (_isLoading || MutationBusy || route.IsPreviewing || !_routes.Contains(route) || !Changed(route)) return;
        var extension = route.Extension;
        _lastActiveExtension = extension;
        var target = route.SelectedChunkerId;
        var version = route.Version;
        route.IsApplying = true;
        route.Error = string.Empty;
        StateHasChanged();
        try
        {
            if (route.Preview is null) await PreviewCoreAsync(route, version, target);
            if (!IsCurrent(route, version, target) || route.Preview is null) return;
            var preview = route.Preview;
            if (!string.Equals(preview.TargetChunkerId, target, StringComparison.OrdinalIgnoreCase)) return;
            if (preview.AffectedDocumentCount > 0 && !await ConfirmAsync(preview.AffectedDocumentCount)) return;
            if (!IsCurrent(route, version, target)) return;
            var result = await ChunkerService.ApplyRoutingChangeAsync(extension, target);
            if (_lifetime.Retired) return;
            var error = result.WithoutData();
            var applied = result.Data;
            if (result.IsFailure() || applied is null)
            {
                route.Error = error.Message ?? L["KnowledgeWorkbench:Chunking:ApplyFailed"];
                Snackbar.Add(route.Error, Severity.Error);
                return;
            }
            Snackbar.Add(L["KnowledgeWorkbench:Chunking:ApplyOutcome", applied.MarkedPendingDocumentCount], Severity.Success);
            await LoadStateCoreAsync();
        }
        finally
        {
            route.IsApplying = false;
            QueueFocusRestoration(extension);
        }
    });
    private void QueueFocusRestoration(string? extension)
    {
        if (_lifetime.Retired) return;
        _restoreFocusExtension = extension ?? string.Empty;
        ++_focusGeneration;
    }

    protected override Task OnAfterRenderAsync(bool firstRender) => _lifetime.RunAsync(async () =>
    {
        if (_restoreFocusExtension is null || _isLoading || MutationBusy) return;
        var extension = _restoreFocusExtension;
        var generation = _focusGeneration;
        var stateGeneration = _stateGeneration;
        var target = _routes.FirstOrDefault(route => string.Equals(route.Extension, extension, StringComparison.OrdinalIgnoreCase))
            ?.TargetControl ?? _routes.FirstOrDefault()?.TargetControl;
        var refresh = _refreshButton;
        _restoreFocusExtension = null;
        if (_lifetime.Retired || generation != _focusGeneration || stateGeneration != _stateGeneration) return;
        try
        {
            if (target is not null && string.IsNullOrEmpty(_loadError)) await target.FocusAsync();
            else if (refresh is not null && !_isTesting) await refresh.FocusAsync();
        }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) when (_lifetime.Retired) { }
    });

    private async Task<bool> ConfirmAsync(int affectedCount)
    {
        var title = L["RAG:Chunker:ConfirmTitle"].Value;
        RenderFragment heading = builder =>
        {
            builder.OpenComponent<MoDialogHeading>(0);
            builder.AddAttribute(1, nameof(MoDialogHeading.Text), title);
            builder.CloseComponent();
        };
        var parameters = new DialogParameters
        {
            { nameof(MudMessageBox.TitleContent), heading },
            { nameof(MudMessageBox.Message), L["RAG:Chunker:ConfirmMessage", affectedCount].Value },
            { nameof(MudMessageBox.YesText), L["RAG:Chunker:Apply"].Value },
            { nameof(MudMessageBox.CancelText), L["Common:Cancel"].Value }
        };
        var dialog = await DialogService.ShowAsync<MudMessageBox>(title, parameters);
        if (!_lifetime.OwnDialog(dialog)) return false;
        try
        {
            var result = await dialog.Result;
            return !_lifetime.Retired && result is { Canceled: false, Data: true };
        }
        finally { _lifetime.ReleaseDialog(dialog); }
    }
    private Task RunChunkerTestAsync() => _lifetime.RunAsync(async () =>
    {
        if (_isLoading || _isTesting || string.IsNullOrWhiteSpace(_testChunkerId)) return;
        var generation = _stateGeneration;
        var id = _testChunkerId;
        var name = EffectiveTestName;
        var content = _testContent;
        _isTesting = true;
        _testResult = null;
        _testError = string.Empty;
        StateHasChanged();
        try
        {
            var result = await ChunkerService.TestChunkerAsync(id, name, content);
            if (_lifetime.Retired || generation != _stateGeneration) return;
            var error = result.WithoutData();
            var test = result.Data;
            if (result.IsFailure()) _testError = error.Message ?? L["KnowledgeWorkbench:Chunking:TestFailed"];
            else _testResult = test;
        }
        finally { if (!_lifetime.Retired) _isTesting = false; }
    });
    private Task ShowTestChunkPreviewAsync() => _lifetime.RunAsync(async () =>
    {
        var test = _testResult;
        if (test is null) return;
        var parameters = new DialogParameters
        {
            { nameof(ChunkViewerDialog.DocumentName), test.DocumentName },
            { nameof(ChunkViewerDialog.OriginalText), test.OriginalText },
            { nameof(ChunkViewerDialog.Chunks), test.Chunks },
            { nameof(ChunkViewerDialog.MatchedChunkIndex), null },
            { nameof(ChunkViewerDialog.StatusText), L["RAG:Chunker:PreviewStatus", test.ChunkerId].Value }
        };
        var dialog = await DialogService.ShowAsync<ChunkViewerDialog>(L["RAG:Chunker:PreviewTitle"].Value, parameters,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true, CloseButton = true });
        _lifetime.OwnDialog(dialog);
    });
    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    private sealed class RouteEditor
    {
        public required string Extension { get; init; }
        public required string CurrentChunkerId { get; init; }
        public required IReadOnlyList<string> CandidateChunkerIds { get; init; }
        public required string SelectedChunkerId { get; set; }
        public MudSelect<string>? TargetControl { get; set; }
        public long Version { get; set; }
        public bool IsApplying { get; set; }
        public bool IsPreviewing { get; set; }
        public string Error { get; set; } = string.Empty;
        public ChunkerRoutingChangePreview? Preview { get; set; }
    }
}
