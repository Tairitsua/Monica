using System.Collections.Immutable;
using Microsoft.AspNetCore.Components.Web;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.RAG.Models;
using Monica.AI.UI.UIRAG.Components;
using Monica.AI.UI.UIRAG.Support;
using Monica.Core.Results;
using MudBlazor;
using KnowledgeBaseModel = Monica.AI.KnowledgeBase.Models.KnowledgeBase;

namespace Monica.AI.UI.Pages;

public partial class RAGDebugPage : IAsyncDisposable
{
    public const string PAGE_URL = "/ai/rag/debug";
    private readonly RetrievalWorkbenchLifetime _lifetime = new();
    private IReadOnlyList<KnowledgeBaseModel> _knowledgeBases = [];
    private IReadOnlyList<EmbeddingModelOption> _embeddingModels = [];
    private IReadOnlyList<TextSearchResult> _searchResults = [];
    private HashSet<string> _selectedKbIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _scopeOpen;
    private void ToggleScope() => _scopeOpen = !_scopeOpen;
    private void CloseScope() => _scopeOpen = false;
    private string _scopeFilter = string.Empty;
    private string _searchQuery = string.Empty;
    private int _topK = 5;
    private bool _isLoading;
    private bool _isOpeningViewer;
    private bool _isSearching;
    private string _selectedEmbeddingModelKey = string.Empty;
    private bool _hasExplicitEmbeddingChoice;
    private string _loadError = string.Empty;
    private string _modelError = string.Empty;
    private string _searchErrorMessage = string.Empty;
    private string _embeddingWarningMessage = string.Empty;
    private long _searchGeneration;
    private SearchSnapshot? _submitted;
    private IEnumerable<KnowledgeBaseModel> VisibleBases => _knowledgeBases.Where(kb =>
        string.IsNullOrWhiteSpace(_scopeFilter) || kb.Name.Contains(_scopeFilter, StringComparison.OrdinalIgnoreCase)
        || kb.Id.Contains(_scopeFilter, StringComparison.OrdinalIgnoreCase)
        || (kb.Description?.Contains(_scopeFilter, StringComparison.OrdinalIgnoreCase) ?? false));
    private bool InputsChanged => _submitted is not null &&
        (_submitted.Query != _searchQuery || _submitted.TopK != _topK
        || _submitted.ModelKey != _selectedEmbeddingModelKey || !_selectedKbIds.SetEquals(_submitted.Ids));

    protected override Task OnInitializedAsync() => LoadAsync();
    private Task LoadAsync() => _lifetime.RunAsync(async () =>
    {
        if (_isLoading) return;
        _isLoading = true;
        _loadError = _modelError = string.Empty;
        var kbResult = await KnowledgeBaseFacade.GetAllAsync();
        if (_lifetime.Retired) return;
        var error = kbResult.WithoutData();
        var kbs = kbResult.Data;
        if (kbResult.IsFailure() || kbs is null) _loadError = error.Message ?? L["KnowledgeWorkbench:Retrieval:LoadFailed"];
        else
        {
            _knowledgeBases = kbs;
            _selectedKbIds = kbs.Select(kb => kb.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var modelsResult = await EmbeddingService.GetEmbeddingModelsAsync();
        if (_lifetime.Retired) return;
        var modelError = modelsResult;
        var models = modelsResult.Data;
        if (modelsResult.IsFailure() || models is null) _modelError = modelError.Message ?? L["KnowledgeWorkbench:Retrieval:ModelsFailed"];
        else _embeddingModels = models;
        RefreshEmbeddingModelSelection();
        CheckEmbeddingModelMismatch();
        _isLoading = false;
    });

    private void SelectAllKBs()
    {
        _selectedKbIds = _knowledgeBases.Select(kb => kb.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RefreshEmbeddingModelSelection();
        CheckEmbeddingModelMismatch();
    }
    private void DeselectAllKBs()
    {
        _selectedKbIds = new(StringComparer.OrdinalIgnoreCase);
        RefreshEmbeddingModelSelection();
        CheckEmbeddingModelMismatch();
    }
    private void OnKBCheckChanged(string id, bool selected)
    {
        if (selected) _selectedKbIds.Add(id); else _selectedKbIds.Remove(id);
        RefreshEmbeddingModelSelection();
        CheckEmbeddingModelMismatch();
    }
    private void OnEmbeddingModelChanged(string key)
    {
        _hasExplicitEmbeddingChoice = true;
        _selectedEmbeddingModelKey = key;
        CheckEmbeddingModelMismatch();
    }
    private void RefreshEmbeddingModelSelection()
    {
        if (_hasExplicitEmbeddingChoice) return;
        var selected = _knowledgeBases.Where(kb => _selectedKbIds.Contains(kb.Id)).ToArray();
        var keys = selected.Select(GetKnowledgeBaseModelKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _selectedEmbeddingModelKey = selected.Length > 0 && keys.Length == 1 && HasAvailableEmbeddingModel(keys[0])
            ? keys[0] : string.Empty;
    }
    private void CheckEmbeddingModelMismatch()
    {
        var mismatched = _knowledgeBases.Where(kb => _selectedKbIds.Contains(kb.Id) && HasEmbeddingBinding(kb)
            && !string.Equals(GetKnowledgeBaseModelKey(kb), _selectedEmbeddingModelKey, StringComparison.OrdinalIgnoreCase))
            .Select(kb => $"{kb.Name}: {GetKnowledgeBaseModelDisplay(kb)}").ToArray();
        _embeddingWarningMessage = !string.IsNullOrEmpty(_selectedEmbeddingModelKey) && mismatched.Length > 0
            ? L["RAG:EmbeddingModel:Warning:SearchMismatch", string.Join("; ", mismatched)].Value : string.Empty;
    }
    private Task HandleSearch() => _lifetime.RunAsync(async () =>
    {
        if (_isLoading || _isSearching || string.IsNullOrWhiteSpace(_searchQuery) || _selectedKbIds.Count == 0) return;
        var request = new SearchSnapshot(_searchQuery, _selectedKbIds.ToImmutableArray(),
            _knowledgeBases.Where(kb => _selectedKbIds.Contains(kb.Id)).Select(kb => kb.Name).ToImmutableArray(),
            _topK, _selectedEmbeddingModelKey,
            string.IsNullOrEmpty(_selectedEmbeddingModelKey) ? L["KnowledgeWorkbench:Retrieval:UseOwnModel"].Value : GetModelDisplayByKey(_selectedEmbeddingModelKey));
        var embeddingOverride = BuildSearchEmbeddingOverride();
        var generation = ++_searchGeneration;
        _submitted = request;
        _isSearching = true;
        _searchResults = [];
        _searchErrorMessage = string.Empty;
        StateHasChanged();
        try
        {
            var result = await SearchFacade.SearchAsync(request.Query, request.Ids, request.TopK, embeddingOverride, _lifetime.Token);
            if (_lifetime.Retired || generation != _searchGeneration) return;
            var error = result.WithoutData();
            var results = result.Data;
            if (result.IsFailure() || results is null)
            {
                _searchErrorMessage = error.Message ?? L["KnowledgeWorkbench:Retrieval:SearchFailed"];
                Snackbar.Add(_searchErrorMessage, Severity.Error);
            }
            else _searchResults = results;
        }
        finally { if (!_lifetime.Retired && generation == _searchGeneration) _isSearching = false; }
    });
    private Task HandleSearchKeyDown(KeyboardEventArgs e) => e.Key == "Enter" ? HandleSearch() : Task.CompletedTask;
    private string GetResultBaseName(TextSearchResult result) => _knowledgeBases.FirstOrDefault(kb =>
        string.Equals(kb.Id, result.KnowledgeBaseId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? result.KnowledgeBaseId ?? L["RAG:Search:Result:MissingKnowledgeBase"].Value;

    private Task ShowChunkViewer(TextSearchResult result) => _lifetime.RunAsync(async () =>
    {
        if (_isOpeningViewer) return;
        _isOpeningViewer = true;
        try
        {
        var baseId = result.KnowledgeBaseId;
        var documentId = result.DocumentId;
        var chunkIndex = result.ChunkIndex;
        if (string.IsNullOrWhiteSpace(documentId)) { Snackbar.Add(L["RAG:Search:Result:MissingDocumentId"], Severity.Warning); return; }
        if (string.IsNullOrWhiteSpace(baseId)) { Snackbar.Add(L["RAG:Search:Result:MissingKnowledgeBase"], Severity.Warning); return; }
        var sourceName = result.SourceName ?? L["RAG:Search:Result:UnknownSource"].Value;
        var statusText = L["RAG:ChunkViewer:SourceKnowledgeBase", GetResultBaseName(result)].Value;
        var chunksResult = await IndexingFacade.GetDocumentChunksAsync(baseId, documentId);
        if (_lifetime.Retired) return;
        var error = chunksResult.WithoutData();
        var view = chunksResult.Data;
        if (chunksResult.IsFailure() || view is null) { Snackbar.Add(error.Message ?? L["Common:Error"], Severity.Error); return; }
        var parameters = new DialogParameters
        {
            { nameof(ChunkViewerDialog.DocumentName), sourceName },
            { nameof(ChunkViewerDialog.DocumentPath), view.DocumentPath },
            { nameof(ChunkViewerDialog.OriginalText), view.OriginalText },
            { nameof(ChunkViewerDialog.Chunks), view.Chunks },
            { nameof(ChunkViewerDialog.MatchedChunkIndex), chunkIndex },
            { nameof(ChunkViewerDialog.StatusText), statusText },
            { nameof(ChunkViewerDialog.SourceKind), view.SourceKind },
            { nameof(ChunkViewerDialog.SourceGroupKey), view.SourceGroupKey }
        };
        var dialog = await DialogService.ShowAsync<ChunkViewerDialog>(L["RAG:ChunkViewer:Title"].Value, parameters,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true, CloseButton = true });
        _lifetime.OwnDialog(dialog);
        }
        finally { _isOpeningViewer = false; }
    });
    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    private sealed record SearchSnapshot(string Query, ImmutableArray<string> Ids, ImmutableArray<string> Names, int TopK, string ModelKey, string ModelDisplay);

    private static bool HasEmbeddingBinding(KnowledgeBaseModel kb)
        => !string.IsNullOrWhiteSpace(kb.EmbeddingProviderId)
           && !string.IsNullOrWhiteSpace(kb.EmbeddingModelName);

    private static string GetKnowledgeBaseModelKey(KnowledgeBaseModel kb)
    {
        if (!HasEmbeddingBinding(kb))
        {
            return string.Empty;
        }

        return EmbeddingModelOption.ToModelKey(kb.EmbeddingProviderId!, kb.EmbeddingModelName!);
    }

    private string GetKnowledgeBaseModelDisplay(KnowledgeBaseModel kb)
    {
        if (!HasEmbeddingBinding(kb))
        {
            return string.Empty;
        }

        var providerLabel = _embeddingModels
            .FirstOrDefault(m => string.Equals(m.ProviderId, kb.EmbeddingProviderId, StringComparison.OrdinalIgnoreCase))
            ?.ProviderDisplayName ?? GetProviderDisplayLabel(kb.EmbeddingProviderId, kb.EmbeddingModelName);

        return $"{kb.EmbeddingModelName} ({providerLabel})";
    }

    private bool HasAvailableEmbeddingModel(string modelKey)
        => _embeddingModels.Any(m =>
            string.Equals(m.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase));

    private string GetModelDisplayByKey(string modelKey)
    {
        var model = _embeddingModels.FirstOrDefault(m =>
            string.Equals(m.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase));
        if (model is not null)
        {
            return $"{model.ModelName} ({model.ProviderDisplayName})";
        }

        if (!EmbeddingModelOption.TryParseModelKey(modelKey, out var providerId, out var modelName))
        {
            return modelKey;
        }

        return $"{modelName} ({GetProviderDisplayLabel(providerId, modelName)})";
    }

    private string GetProviderDisplayLabel(string? providerId, string? modelName = null)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return string.Empty;
        }

        var model = _embeddingModels.FirstOrDefault(m =>
            string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(modelName)
                || string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase)));
        if (model is not null)
        {
            return model.ProviderDisplayName;
        }

        var provider = ProviderFactory.GetProviderInfo(providerId);
        if (provider is not null)
        {
            return provider.DisplayName;
        }

        return providerId.Trim();
    }

    private RAGSearchEmbeddingOverride? BuildSearchEmbeddingOverride()
    {
        if (!EmbeddingModelOption.TryParseModelKey(_selectedEmbeddingModelKey, out var providerId, out var modelName))
        {
            return null;
        }

        return new RAGSearchEmbeddingOverride(providerId, modelName);
    }
}
