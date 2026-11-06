using Monica.AI.KnowledgeBase.Models;
using Monica.AI.RAG.Facades;
using Monica.AI.RAG.Models;
using Monica.AI.UI.UIRAG.Components;
using Monica.Core.Results;
using MudBlazor;
using KnowledgeBaseModel = Monica.AI.KnowledgeBase.Models.KnowledgeBase;

namespace Monica.AI.UI.UIRAG.State;

public sealed partial class RAGManagePageState
{
    /// <summary>
    /// Changes the current embedding selection and immediately updates RAG support when already enabled.
    /// </summary>
    public Task ChangeEmbeddingModelAsync(string modelKey)
        => _session.RunAsync(() => ChangeEmbeddingModelAsyncCore(modelKey), selectionBound: true);

    private async Task ChangeEmbeddingModelAsyncCore(string modelKey)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        CurrentEmbeddingModelKey = modelKey;
        NotifyStateChanged();

        if (HasEmbeddingBinding(originKnowledgeBase))
        {
            await EnableOrUpdateRagSupportAsync();
            _session.CheckCurrent();
        }
    }

    /// <summary>
    /// Enables RAG support for the selected knowledge base or switches its embedding model.
    /// </summary>
    public Task EnableOrUpdateRagSupportAsync()
        => _session.RunAsync(() => EnableOrUpdateRagSupportAsyncCore(), selectionBound: true);

    private async Task EnableOrUpdateRagSupportAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var selectedKnowledgeBaseId = originKnowledgeBase.Id;
        var isNewRagBinding = !HasEmbeddingBinding(originKnowledgeBase);

        if (!EmbeddingModelOption.TryParseModelKey(CurrentEmbeddingModelKey, out var providerId, out var modelName))
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: invalid embedding model key.", Severity.Error);
            return;
        }

        var previousModelKey = GetKnowledgeBaseModelKey(originKnowledgeBase);
        if (HasEmbeddingBinding(originKnowledgeBase)
            && string.Equals(previousModelKey, CurrentEmbeddingModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var requiresReindexWarning = HasIndexedContent(originKnowledgeBase)
            && !string.IsNullOrWhiteSpace(previousModelKey);
        if (requiresReindexWarning)
        {
            var confirmed = await _session.ShowMessageBoxAsync(
                _localizer["RAG:EmbeddingModel:ReindexConfirm:Title"],
                _localizer["RAG:EmbeddingModel:ReindexConfirm:Message", originKnowledgeBase.Name],
                yesText: _localizer["RAG:EmbeddingModel:ReindexConfirm:Continue"],
                cancelText: _localizer["Common:Cancel"]);
            _session.CheckCurrent();

            if (confirmed != true)
            {
                CurrentEmbeddingModelKey = previousModelKey;
                NotifyStateChanged();
                return;
            }
        }

        if (!TryBeginRagSupportAction(selectedKnowledgeBaseId, RagSupportActionKind.EnableOrUpdate))
        {
            return;
        }

        try
        {
            if (isNewRagBinding && !await EnsureVectorCollectionReusableAsync(selectedKnowledgeBaseId))
            {
                return;
            }

            var bindingResult = await _embeddingFacade.SetKnowledgeBaseEmbeddingModelAsync(
                    selectedKnowledgeBaseId,
                    providerId,
                    modelName,
                    clearIndex: true);
            _session.CheckCurrent();

            var error = bindingResult;
            if (bindingResult.IsFailure())
            {
                CurrentEmbeddingModelKey = previousModelKey;
                _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
                NotifyStateChanged();
                return;
            }

            await RefreshSelectedKnowledgeBaseAsync();
            _session.CheckCurrent();
            await LoadDocumentQueueAsync();
            _session.CheckCurrent();

            _snackbar.Add(
                requiresReindexWarning
                    ? _localizer["RAG:EmbeddingModel:ReindexRequired"]
                    : _localizer["RAG:RagSupport:Enabled"],
                requiresReindexWarning ? Severity.Warning : Severity.Success);
        }
        finally
        {
            if (_session.CanPublish)
            {
                EndRagSupportAction(selectedKnowledgeBaseId);
            }
        }
    }

    /// <summary>
    /// Removes RAG support and clears existing RAG vector/index data for the selected knowledge base.
    /// </summary>
    public Task RemoveRagSupportAsync()
        => _session.RunAsync(() => RemoveRagSupportAsyncCore(), selectionBound: true);

    private async Task RemoveRagSupportAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || !HasEmbeddingBinding(originKnowledgeBase))
        {
            return;
        }

        var selectedKnowledgeBaseId = originKnowledgeBase.Id;
        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["RAG:RagSupport:RemoveConfirm:Title"],
            _localizer["RAG:RagSupport:RemoveConfirm:Message", originKnowledgeBase.Name],
            yesText: _localizer["RAG:RagSupport:RemoveConfirm:Confirm"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        if (confirmed != true)
        {
            return;
        }

        if (!TryBeginRagSupportAction(selectedKnowledgeBaseId, RagSupportActionKind.Remove))
        {
            return;
        }

        try
        {
            var result = await _vectorStoreFacade.RemoveSupportAsync(selectedKnowledgeBaseId);
            _session.CheckCurrent();
            var error = result.WithoutData();
            var removal = result.Data;
            if (result.IsFailure() || removal is null)
            {
                if (result.IsFailure() && CanForceRemoveRagSupport(error)
                    && await ConfirmForceRemoveRagSupportAsync())
                {
                    await ForceRemoveRagSupportAsync(selectedKnowledgeBaseId);
                    _session.CheckCurrent();
                    return;
                }

                _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
                return;
            }

            await CompleteRagSupportRemovalAsync(removal);
            _session.CheckCurrent();
        }
        finally
        {
            if (_session.CanPublish)
            {
                EndRagSupportAction(selectedKnowledgeBaseId);
            }
        }
    }

    /// <summary>
    /// Opens the embedding model diagnostics dialog.
    /// </summary>
    public Task ShowEmbeddingDiagnosticsDialogAsync()
        => _session.RunAsync(() => ShowEmbeddingDiagnosticsDialogAsyncCore(), selectionBound: false);

    private async Task ShowEmbeddingDiagnosticsDialogAsyncCore()
    {
        _session.CheckCurrent();
        await _session.ShowAsync<EmbeddingDiagnosticsDialog>(
            _localizer["RAG:EmbeddingDiagnostics:Title"],
            new DialogParameters(),
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        _session.CheckCurrent();
    }

    /// <summary>
    /// Opens the vector-store diagnostics dialog.
    /// </summary>
    public Task ShowVectorStoreDiagnosticsDialogAsync()
        => _session.RunAsync(() => ShowVectorStoreDiagnosticsDialogAsyncCore(), selectionBound: false);

    private async Task ShowVectorStoreDiagnosticsDialogAsyncCore()
    {
        _session.CheckCurrent();
        await _session.ShowAsync<VectorStoreDiagnosticsDialog>(
            _localizer["RAG:VectorDiagnostics:Title"],
            new DialogParameters(),
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        _session.CheckCurrent();
    }

    /// <summary>
    /// Start batch indexing for selected pending or failed queue rows.
    /// </summary>
    /// <remarks>The process-owned batch continues after this visit retires. Retirement drains its receipt;
    /// only the explicit cancel action requests cancellation of the active batch.</remarks>
    public Task StartBatchIndexingAsync()
        => _session.RunAsync(() => StartBatchIndexingAsyncCore(), selectionBound: true);

    private async Task StartBatchIndexingAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        if (!EnsureEmbeddingModelConfiguredForIndexing())
        {
            return;
        }

        var selectedKnowledgeBaseId = originKnowledgeBase.Id;
        var originQueue = DocumentQueue.ToArray();
        var originSelectedIds = SelectedDocumentIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originParallelCount = ParallelCount;
        if (!await EnsureVectorCollectionReusableAsync(selectedKnowledgeBaseId))
        {
            return;
        }

        var selectedDocuments = originQueue
            .Where(document => originSelectedIds.Contains(document.Id)
                               && document.Status is DocumentStatus.Pending or DocumentStatus.Error)
            .ToList();
        if (selectedDocuments.Count == 0)
        {
            _snackbar.Add(_localizer["RAG:ParallelIndexing:NoPendingDocuments"], Severity.Warning);
            return;
        }

        _batchStartInFlightKnowledgeBaseId = selectedKnowledgeBaseId;
        EnsureQueuePolling();
        NotifyStateChanged();

        var generation = _session.Generation;
        var progress = new Progress<IndexingProgress>(_ => OnBatchIndexingProgress(selectedKnowledgeBaseId, generation));
        try
        {
            _session.CheckCurrent();
            // Admission/preflight belongs to the visit; the durable run belongs to the process.
            var result = await _indexingFacade.StartBatchAsync(
                selectedKnowledgeBaseId,
                selectedDocuments.Select(static document => document.Id),
                originParallelCount,
                progress,
                ct: CancellationToken.None);
            _session.CheckCurrent();

            var error = result;
            if (result.IsFailure())
            {
                if (IsCancellationMessage(error.Message))
                {
                    _snackbar.Add(_localizer["Error:RequestCancelled"], Severity.Warning);
                }
                else
                {
                    _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
                    await _session.ShowMessageBoxAsync(
                        _localizer["Common:Error"],
                        error.Message ?? _localizer["Common:Error"].Value,
                        yesText: _localizer["Common:Close"]);
                    _session.CheckCurrent();
                }
            }
            else
            {
                _snackbar.Add(_localizer["Common:Success"], Severity.Success);
            }
        }
        finally
        {
            if (_session.CanPublish)
            {
                if (string.Equals(
                        _batchStartInFlightKnowledgeBaseId,
                        selectedKnowledgeBaseId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _batchStartInFlightKnowledgeBaseId = null;
                }

                await RefreshSelectedKnowledgeBaseAsync();
                _session.CheckCurrent();
                await LoadDocumentQueueAsync(ensurePolling: false);
                _session.CheckCurrent();
                EnsureQueuePolling();
                NotifyStateChanged();
            }
        }
    }

    /// <summary>
    /// Start indexing for one queue row.
    /// </summary>
    /// <remarks>After admission, indexing continues independently of route retirement. The visit still
    /// drains the actual operation and suppresses late progress, refreshes, and notifications.</remarks>
    public Task StartDocumentIndexingAsync(DocumentQueueItem document)
        => _session.RunAsync(() => StartDocumentIndexingAsyncCore(document), selectionBound: true);

    private async Task StartDocumentIndexingAsyncCore(DocumentQueueItem document)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || !string.Equals(originKnowledgeBase.Id, document.KnowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;

        if (!EnsureEmbeddingModelConfiguredForIndexing())
        {
            return;
        }

        var selectedKnowledgeBaseId = originKnowledgeBase.Id;
        if (!await EnsureVectorCollectionReusableAsync(selectedKnowledgeBaseId))
        {
            return;
        }

        _singleIndexInFlightKnowledgeBaseId = selectedKnowledgeBaseId;
        _singleIndexInFlightDocumentId = document.Id;
        EnsureQueuePolling();
        NotifyStateChanged();

        try
        {
            var generation = _session.Generation;
            var progress = new Progress<IndexingProgress>(_ => OnBatchIndexingProgress(selectedKnowledgeBaseId, generation));
            _session.CheckCurrent();
            var result = await _indexingFacade.IndexDocumentAsync(
                selectedKnowledgeBaseId,
                document.Id,
                progress,
                ct: CancellationToken.None);
            _session.CheckCurrent();
            var error = result;
            if (result.IsFailure())
            {
                _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
                return;
            }

            _snackbar.Add(_localizer["Common:Success"], Severity.Success);
        }
        finally
        {
            if (_session.CanPublish)
            {
                ClearSingleIndexInFlight();
                await RefreshSelectedKnowledgeBaseAsync();
                _session.CheckCurrent();
                await LoadDocumentQueueAsync(ensurePolling: false);
                _session.CheckCurrent();
                EnsureQueuePolling();
                NotifyStateChanged();
            }
        }
    }

    /// <summary>
    /// Cancel batch indexing for the current knowledge base.
    /// </summary>
    public Task CancelBatchIndexingAsync()
        => _session.RunAsync(() => CancelBatchIndexingAsyncCore(), selectionBound: true);

    private async Task CancelBatchIndexingAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var cancelResult = await _indexingFacade.CancelBatchAsync(originKnowledgeBase.Id);
        _session.CheckCurrent();
        var error = cancelResult;
        if (cancelResult.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        ClearBatchStartInFlight();
        EnsureQueuePolling();
        _snackbar.Add(cancelResult.Message ?? _localizer["Error:RequestCancelled"].Value, Severity.Warning);
        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        NotifyStateChanged();
    }

    /// <summary>
    /// Clear pending and failed queue rows for the current selection.
    /// </summary>
    public Task ClearDocumentQueueAsync()
        => _session.RunAsync(() => ClearDocumentQueueAsyncCore(), selectionBound: true);

    private async Task ClearDocumentQueueAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["Common:Confirm"],
            _localizer["RAG:DocumentQueue:ClearQueueConfirm", originKnowledgeBase.Name],
            yesText: _localizer["RAG:DocumentQueue:Actions:ClearQueue"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        if (confirmed != true)
        {
            return;
        }

        var result = await _indexingFacade.ClearPendingQueueAsync(originKnowledgeBase.Id);
        _session.CheckCurrent();
        var error = result.WithoutData();
        var removedCount = result.Data;
        if (result.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["RAG:DocumentQueue:Messages:QueueCleared", removedCount], Severity.Success);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Queue all indexed documents in the selected knowledge base for reindexing.
    /// </summary>
    public Task ReindexSelectedKnowledgeBaseAsync()
        => _session.RunAsync(() => ReindexSelectedKnowledgeBaseAsyncCore(), selectionBound: true);

    private async Task ReindexSelectedKnowledgeBaseAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var result = await _indexingFacade.QueueKnowledgeBaseForReindexAsync(originKnowledgeBase.Id);
        _session.CheckCurrent();
        var error = result.WithoutData();
        var queuedCount = result.Data;
        if (result.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["RAG:VectorValidation:Messages:ReindexQueued", queuedCount], Severity.Success);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
    }

    private bool HasIndexedContent(KnowledgeBaseModel knowledgeBase)
    {
        if (knowledgeBase.ChunkCount > 0)
        {
            return true;
        }

        return DocumentQueue.Any(static item => item.Status == DocumentStatus.Done || item.ChunkCount > 0);
    }

    private async Task<bool> EnsureVectorCollectionReusableAsync(string knowledgeBaseId)
    {
        _session.CheckCurrent();
        var knowledgeBase = SelectedKnowledgeBase;
        if (knowledgeBase is null)
        {
            return false;
        }

        if (HasIndexedContent(knowledgeBase))
        {
            return true;
        }

        var statusResult = await _vectorStoreFacade.GetCollectionStatusAsync(knowledgeBaseId);
        _session.CheckCurrent();
        var statusError = statusResult.WithoutData();
        var status = statusResult.Data;
        if (statusResult.IsFailure() || status is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {statusError.Message}", Severity.Error);
            return false;
        }

        if (!status.WasChecked)
        {
            _snackbar.Add(_localizer["RAG:VectorCollection:CheckUnavailable"], Severity.Warning);
            return true;
        }

        if (!status.Exists)
        {
            return true;
        }

        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["RAG:VectorCollection:OverwriteConfirm:Title"],
            _localizer["RAG:VectorCollection:OverwriteConfirm:Message", status.CollectionName],
            yesText: _localizer["RAG:VectorCollection:OverwriteConfirm:Confirm"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        if (confirmed != true)
        {
            return false;
        }

        var overwriteResult = await _vectorStoreFacade.OverwriteCollectionAsync(knowledgeBaseId);
        _session.CheckCurrent();
        var overwriteError = overwriteResult.WithoutData();
        var overwrite = overwriteResult.Data;
        if (overwriteResult.IsFailure() || overwrite is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {overwriteError.Message}", Severity.Error);
            return false;
        }

        SelectedKnowledgeBaseVectorValidation = null;
        _snackbar.Add(
            _localizer[
                "RAG:VectorCollection:Overwritten",
                overwrite.CollectionName,
                overwrite.ResetDocumentCount,
                overwrite.ClearedChunkCount],
            Severity.Warning);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
        return true;
    }

    private async Task<bool> ConfirmForceRemoveRagSupportAsync()
    {
        _session.CheckCurrent();
        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["RAG:RagSupport:ForceRemoveConfirm:Title"],
            _localizer["RAG:RagSupport:ForceRemoveConfirm:Message"],
            yesText: _localizer["RAG:RagSupport:ForceRemoveConfirm:Confirm"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        return confirmed == true;
    }

    private async Task ForceRemoveRagSupportAsync(string knowledgeBaseId)
    {
        _session.CheckCurrent();
        var forcedResult = await _vectorStoreFacade.RemoveSupportAsync(
            knowledgeBaseId,
            forceLocalMetadataRemoval: true);
        _session.CheckCurrent();
        var forceError = forcedResult.WithoutData();
        var forcedRemoval = forcedResult.Data;
        if (forcedResult.IsFailure() || forcedRemoval is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {forceError.Message}", Severity.Error);
            return;
        }

        await CompleteRagSupportRemovalAsync(forcedRemoval);
        _session.CheckCurrent();
    }

    private async Task CompleteRagSupportRemovalAsync(KnowledgeBaseRagSupportRemovalResult removal)
    {
        _session.CheckCurrent();
        CurrentEmbeddingModelKey = string.Empty;
        SelectedKnowledgeBaseVectorValidation = null;
        _snackbar.Add(
            _localizer[
                removal.WasForced
                    ? "RAG:RagSupport:ForceRemoved"
                    : "RAG:RagSupport:Removed",
                removal.ResetDocumentCount,
                removal.ClearedChunkCount],
            removal.WasForced ? Severity.Warning : Severity.Success);

        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
    }

    private static bool CanForceRemoveRagSupport(Result error)
    {
        if (error.Metadata is null)
        {
            return false;
        }

        var metadata = (IDictionary<string, object?>)error.Metadata;
        return metadata.TryGetValue(RAGVectorStoreFacade.CAN_FORCE_REMOVE_RAG_SUPPORT_METADATA_KEY, out var canForce)
               && canForce is true;
    }
}
