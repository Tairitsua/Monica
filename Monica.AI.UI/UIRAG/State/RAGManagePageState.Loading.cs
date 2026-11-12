using Monica.AI.KnowledgeBase.Models;
using Monica.Core.Results;
using MudBlazor;
using DocumentQueueItemModel = Monica.AI.KnowledgeBase.Models.DocumentQueueItem;
using KnowledgeBaseModel = Monica.AI.KnowledgeBase.Models.KnowledgeBase;

namespace Monica.AI.UI.UIRAG.State;

public sealed partial class RAGManagePageState
{
    private long _vectorValidationRevision;
    private long _queueReadRevision;
    private long _catalogReadRevision;
    private long _selectedMetadataRevision;
    /// <summary>
    /// Initialize the page for the current visit.
    /// </summary>
    public Task InitializeAsync()
        => _session.RunAsync(() => InitializeAsyncCore(), selectionBound: false);

    private async Task InitializeAsyncCore()
    {
        _session.CheckCurrent();
        Attach();

        IsInitialLoading = true;
        UpdateLoadingState(_localizer["RAG:KnowledgeBase:Title"].Value, 35);
        await LoadKnowledgeBasesAsync();
        _session.CheckCurrent();
        await LoadEmbeddingModelsAsync();
        _session.CheckCurrent();
        LoadingProgressValue = 100;
        IsInitialLoading = false;
        NotifyStateChanged();
    }

    /// <summary>
    /// Change the current knowledge-base selection and load dependent data.
    /// </summary>
    public Task SelectKnowledgeBaseAsync(KnowledgeBaseModel? knowledgeBase)
        => _session.RunAsync(() => SelectKnowledgeBaseAsyncCore(knowledgeBase), selectionBound: false);

    private async Task SelectKnowledgeBaseAsyncCore(KnowledgeBaseModel? knowledgeBase)
    {
        _session.CheckCurrent();
        if (!CanChangeKnowledgeBaseSelection)
        {
            return;
        }

        if (!string.Equals(
                _batchStartInFlightKnowledgeBaseId,
                knowledgeBase?.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            ClearBatchStartInFlight();
        }

        if (!string.Equals(
                _singleIndexInFlightKnowledgeBaseId,
                knowledgeBase?.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            ClearSingleIndexInFlight();
        }

        SelectedKnowledgeBase = knowledgeBase;
        _session.AdvanceSelection();
        _queuePollingState.Stop();
        SelectedDocumentIds.Clear();
        DocumentQueue = [];
        IsSelectionLoading = false;
        QueueErrorMessage = string.Empty;
        VectorValidationErrorMessage = string.Empty;
        IsVectorValidationLoading = false;
        SelectedKnowledgeBaseVectorValidation = null;

        if (knowledgeBase is null)
        {
            DocumentQueue = [];
            CurrentEmbeddingModelKey = string.Empty;
            SelectedKnowledgeBaseVectorValidation = null;
            ClearBatchStartInFlight();
            ClearSingleIndexInFlight();
            ClearRagSupportActionInFlight();
            _queuePollingState.Stop();
            NotifyStateChanged();
            return;
        }

        IsSelectionLoading = true;
        UpdateEmbeddingModelSelection(knowledgeBase);
        UpdateLoadingState(_localizer["RAG:DocumentQueue:Title"].Value, 72);
        NotifyStateChanged();

        if (!AreEmbeddingModelsLoaded)
        {
            await LoadEmbeddingModelsAsync();
            _session.CheckCurrent();
        }

        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
        EnsureQueuePolling();
        IsSelectionLoading = false;
        LoadingProgressValue = 100;
        NotifyStateChanged();
        QueueSelectedKnowledgeBaseVectorValidation(knowledgeBase.Id);
    }

    private async Task<bool> LoadKnowledgeBasesAsync()
    {
        _session.CheckCurrent();
        var revision = ++_catalogReadRevision;
        var metadataRevision = Interlocked.Increment(ref _selectedMetadataRevision);
        var catalogResult = await _knowledgeBaseFacade.GetAllAsync();
        _session.CheckCurrent();
        if (revision != _catalogReadRevision) return false;
        var error = catalogResult.WithoutData();
        var knowledgeBases = catalogResult.Data;
        if (catalogResult.IsFailure() || knowledgeBases is null)
        {
            CatalogErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            NotifyStateChanged();
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return false;
        }

        CatalogErrorMessage = string.Empty;
        KnowledgeBases = knowledgeBases.ToList();
        if (SelectedKnowledgeBase is { } selected)
        {
            if (metadataRevision == _selectedMetadataRevision)
            {
                SelectedKnowledgeBase = KnowledgeBases.FirstOrDefault(kb =>
                    string.Equals(kb.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
                if (SelectedKnowledgeBase is null) await SelectKnowledgeBaseAsyncCore(null);
            }
            else
            {
                KnowledgeBases = KnowledgeBases.Select(kb =>
                    string.Equals(kb.Id, selected.Id, StringComparison.OrdinalIgnoreCase) ? selected : kb).ToList();
            }
        }
        NotifyStateChanged();
        return true;
    }

    private async Task<bool> LoadEmbeddingModelsAsync()
    {
        _session.CheckCurrent();
        var modelsResult = await _embeddingFacade.GetEmbeddingModelsAsync();
        _session.CheckCurrent();
        var error = modelsResult.WithoutData();
        var models = modelsResult.Data;
        if (modelsResult.IsFailure() || models is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return false;
        }

        AvailableModels = models.ToList();
        AreEmbeddingModelsLoaded = true;
        if (SelectedKnowledgeBase is not null)
        {
            UpdateEmbeddingModelSelection(SelectedKnowledgeBase);
        }

        NotifyStateChanged();
        return true;
    }

    /// <summary>Retries the selected queue, persisted base metadata, and vector validation through the current visit.</summary>
    public Task ReloadSelectedQueueAsync()
        => _session.RunAsync(async () =>
        {
            await LoadDocumentQueueAsync();
            _session.CheckCurrent();
            if (SelectedKnowledgeBase is { } selected) QueueSelectedKnowledgeBaseVectorValidation(selected.Id);
        });

    private async Task LoadDocumentQueueAsync(bool ensurePolling = true)
    {
        _session.CheckCurrent();
        var revision = ++_queueReadRevision;
        _queuePollingState.Stop();
        if (SelectedKnowledgeBase is null)
        {
            return;
        }

        var originKnowledgeBaseId = SelectedKnowledgeBase.Id;
        var queueResult = await _knowledgeDocumentFacade.GetDocumentInventoryAsync(originKnowledgeBaseId);
        _session.CheckCurrent();
        if (revision != _queueReadRevision) return;

        var error = queueResult.WithoutData();
        var queue = queueResult.Data;
        if (queueResult.IsFailure() || queue is null)
        {
            QueueErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            DocumentQueue = [];
        }
        else
        {
            QueueErrorMessage = string.Empty;
            DocumentQueue = queue.ToList();
            await RefreshSelectedMetadataAsync(originKnowledgeBaseId);
            _session.CheckCurrent();
            if (revision != _queueReadRevision) return;
        }

        RemoveUnavailableDocumentSelections();

        if (ensurePolling)
        {
            EnsureQueuePolling();
        }

        NotifyStateChanged();
    }

    private async Task RefreshSelectedKnowledgeBaseAsync()
    {
        _session.CheckCurrent();
        if (SelectedKnowledgeBase is null)
        {
            SelectedKnowledgeBaseVectorValidation = null;
            ClearBatchStartInFlight();
            ClearSingleIndexInFlight();
            ClearRagSupportActionInFlight();
            NotifyStateChanged();
            return;
        }

        if (!await LoadKnowledgeBasesAsync()) return;
        _session.CheckCurrent();

        if (SelectedKnowledgeBase is null)
        {
            DocumentQueue = [];
            CurrentEmbeddingModelKey = string.Empty;
            SelectedKnowledgeBaseVectorValidation = null;
            ClearSingleIndexInFlight();
            ClearRagSupportActionInFlight();
            NotifyStateChanged();
            return;
        }

        UpdateEmbeddingModelSelection(SelectedKnowledgeBase);
        QueueSelectedKnowledgeBaseVectorValidation(SelectedKnowledgeBase.Id);
        NotifyStateChanged();
    }

    private async Task LoadSelectedKnowledgeBaseVectorValidationAsync(string knowledgeBaseId, long revision, long? pollingGeneration = null)
    {
        _session.CheckCurrent();
        if (SelectedKnowledgeBase is null
            || !string.Equals(SelectedKnowledgeBase.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedKnowledgeBaseVectorValidation = null;
            NotifyStateChanged();
            return;
        }

        var result = await _vectorStoreFacade.ValidateAsync(knowledgeBaseId);
        _session.CheckCurrent();
        if (revision != _vectorValidationRevision) return;
        if (pollingGeneration is { } generation && !_queuePollingState.IsCurrentGeneration(generation)) return;
        if (SelectedKnowledgeBase is null
            || !string.Equals(SelectedKnowledgeBase.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var error = result.WithoutData();
        var validation = result.Data;
        if (result.IsFailure())
        {
            VectorValidationErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            SelectedKnowledgeBaseVectorValidation = null;
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
        }
        else
        {
            VectorValidationErrorMessage = string.Empty;
            SelectedKnowledgeBaseVectorValidation = validation;
        }

        IsVectorValidationLoading = false;
        NotifyStateChanged();
    }

    private void UpdateLoadingState(string phaseText, double progressValue)
    {
        LoadingPhaseText = phaseText;
        LoadingProgressValue = progressValue;
        NotifyStateChanged();
    }

    private void QueueSelectedKnowledgeBaseVectorValidation(string knowledgeBaseId, long? pollingGeneration = null)
    {
        if (_session.IsRetired) return;
        IsVectorValidationLoading = true;
        VectorValidationErrorMessage = string.Empty;
        NotifyStateChanged();
        var revision = ++_vectorValidationRevision;
        _session.StartBackground(() => LoadSelectedKnowledgeBaseVectorValidationAsync(knowledgeBaseId, revision, pollingGeneration));
    }

    private Task OnQueueSettled(string knowledgeBaseId, long generation) => _session.PublishAsync(() =>
    {
        if (!_queuePollingState.IsCurrentGeneration(generation)
            || !string.Equals(SelectedKnowledgeBase?.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        QueueSelectedKnowledgeBaseVectorValidation(knowledgeBaseId, generation);
    });

    private Task OnQueueRefreshed(string knowledgeBaseId, long generation, IReadOnlyList<DocumentQueueItemModel> queue)
        => _session.RunAsync(async () =>
    {
        if (!string.Equals(SelectedKnowledgeBase?.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        if (!_queuePollingState.IsCurrentGeneration(generation)) return;
        var revision = Interlocked.Increment(ref _selectedMetadataRevision);
        var metadata = await _knowledgeBaseFacade.GetByIdAsync(knowledgeBaseId);
        _session.CheckCurrent();
        await _session.PublishAsync(() =>
        {
            if (!_queuePollingState.IsCurrentGeneration(generation)
                || revision != _selectedMetadataRevision) return;
            QueueErrorMessage = string.Empty;
            DocumentQueue = queue.ToList();
            ApplySelectedMetadata(knowledgeBaseId, metadata);
            RemoveUnavailableDocumentSelections();
            NotifyStateChanged();
        });
    });

    private async Task RefreshSelectedMetadataAsync(string knowledgeBaseId)
    {
        var revision = Interlocked.Increment(ref _selectedMetadataRevision);
        var result = await _knowledgeBaseFacade.GetByIdAsync(knowledgeBaseId);
        _session.CheckCurrent();
        if (revision != _selectedMetadataRevision) return;
        ApplySelectedMetadata(knowledgeBaseId, result);
    }

    private void ApplySelectedMetadata(string knowledgeBaseId, Result<KnowledgeBaseModel> result)
    {
        if (!string.Equals(SelectedKnowledgeBase?.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        var error = result.WithoutData();
        var knowledgeBase = result.Data;
        if (result.IsFailure() || knowledgeBase is null)
        {
            CatalogErrorMessage = error.Message ?? _localizer["Common:Error"].Value;
            return;
        }

        CatalogErrorMessage = string.Empty;
        SelectedKnowledgeBase = knowledgeBase;
        KnowledgeBases = KnowledgeBases.Select(kb =>
            string.Equals(kb.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase) ? knowledgeBase : kb).ToList();
    }

    private Task OnQueueRefreshFailed(string knowledgeBaseId, long generation, string message) => _session.PublishAsync(() =>
    {
        if (!string.Equals(SelectedKnowledgeBase?.Id, knowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        if (!_queuePollingState.IsCurrentGeneration(generation)) return;
        QueueErrorMessage = message;
        DocumentQueue = [];
        _snackbar.Add($"{_localizer["Common:Error"]}: {message}", Severity.Error);
        NotifyStateChanged();
    });
}
