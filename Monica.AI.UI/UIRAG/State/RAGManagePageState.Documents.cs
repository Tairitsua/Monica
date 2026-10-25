using Monica.AI.KnowledgeBase.Models;
using Monica.AI.RAG.Models;
using Monica.AI.UI.UIRAG.Components;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.AI.UI.UIRAG.State;

public sealed partial class RAGManagePageState
{
    /// <summary>
    /// Open the chunk viewer dialog for one queue row.
    /// </summary>
    public Task ShowChunkViewerAsync(DocumentQueueItem document) => _session.RunAsync(() => ShowChunkViewerAsyncCore(document), selectionBound: true);

    private async Task ShowChunkViewerAsyncCore(DocumentQueueItem document)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || !string.Equals(originKnowledgeBase.Id, document.KnowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var result = await _indexingFacade.GetDocumentChunksAsync(originKnowledgeBase.Id, document.Id);
        _session.CheckCurrent();
        if (result.IsFailed(out var error, out var view))
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        var statusText = document.Status switch
        {
            DocumentStatus.Done => _localizer["RAG:ChunkViewer:Indexed", document.IndexedAt is { } indexedAt ? _time.FormatDate(indexedAt) : ""].Value,
            DocumentStatus.Pending => _localizer["RAG:ChunkViewer:PendingIndexing"].Value,
            _ => document.Status.ToString()
        };

        var parameters = new DialogParameters
        {
            { nameof(ChunkViewerDialog.DocumentName), document.Name },
            { nameof(ChunkViewerDialog.DocumentPath), view.DocumentPath },
            { nameof(ChunkViewerDialog.OriginalText), view.OriginalText },
            { nameof(ChunkViewerDialog.Chunks), view.Chunks },
            { nameof(ChunkViewerDialog.MatchedChunkIndex), null },
            { nameof(ChunkViewerDialog.StatusText), statusText },
            { nameof(ChunkViewerDialog.SourceKind), view.SourceKind },
            { nameof(ChunkViewerDialog.SourceGroupKey), view.SourceGroupKey }
        };

        await _session.ShowAsync<ChunkViewerDialog>(
            _localizer["RAG:ChunkViewer:Title"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        _session.CheckCurrent();
    }

    /// <summary>
    /// Queue one document for reindexing.
    /// </summary>
    public Task ReindexDocumentAsync(DocumentQueueItem document) => _session.RunAsync(() => ReindexDocumentAsyncCore(document), selectionBound: true);

    private async Task ReindexDocumentAsyncCore(DocumentQueueItem document)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || !string.Equals(originKnowledgeBase.Id, document.KnowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var result = await _indexingFacade.QueueDocumentForReindexAsync(
            originKnowledgeBase.Id,
            document.Id);
        _session.CheckCurrent();
        if (result.IsFailed(out var error))
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["Common:Success"], Severity.Success);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentQueueAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Show the full indexing error for one queue row.
    /// </summary>
    public Task ShowIndexingErrorAsync(DocumentQueueItem document) => _session.RunAsync(() => ShowIndexingErrorAsyncCore(document), selectionBound: true);

    private async Task ShowIndexingErrorAsyncCore(DocumentQueueItem document)
    {
        _session.CheckCurrent();
        var message = string.IsNullOrWhiteSpace(document.ErrorMessage)
            ? _localizer["Common:Error"].Value
            : document.ErrorMessage;

        await _session.ShowMessageBoxAsync(
            _localizer["Common:Error"],
            $"{document.Name}\n\n{message}",
            yesText: _localizer["Common:Close"]);
        _session.CheckCurrent();
    }
}
