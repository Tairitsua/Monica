using Microsoft.AspNetCore.Components.Forms;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.UI.UIKnowledgeBase.Components;
using Monica.Core.Results;
using MudBlazor;

namespace Monica.AI.UI.UIKnowledgeBase.State;

public sealed partial class KnowledgeBaseManagePageState
{
    /// <summary>
    /// Deletes the current knowledge-base selection if present.
    /// </summary>
    public Task DeleteSelectedKnowledgeBaseAsync()
        => _session.RunAsync(() => DeleteSelectedKnowledgeBaseAsyncCore(), selectionBound: true);

    private async Task DeleteSelectedKnowledgeBaseAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        await DeleteKnowledgeBaseAsync(originKnowledgeBase);
        _session.CheckCurrent();
    }

    /// <summary>
    /// Deletes one knowledge base after confirmation.
    /// </summary>
    public Task DeleteKnowledgeBaseAsync(Monica.AI.KnowledgeBase.Models.KnowledgeBase knowledgeBase)
        => _session.RunAsync(() => DeleteKnowledgeBaseAsyncCore(knowledgeBase), selectionBound: true);

    private async Task DeleteKnowledgeBaseAsyncCore(Monica.AI.KnowledgeBase.Models.KnowledgeBase knowledgeBase)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["Common:Confirm"],
            _localizer["KnowledgeBase:Manage:DeleteConfirm", knowledgeBase.Name],
            yesText: _localizer["Common:Delete"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        if (confirmed != true)
        {
            return;
        }

        var deleteResult = await _knowledgeBaseFacade.DeleteAsync(knowledgeBase.Id);
        _session.CheckCurrent();

        var error = deleteResult;
        if (deleteResult.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["Common:Success"], Severity.Success);
        if (string.Equals(originKnowledgeBase?.Id, knowledgeBase.Id, StringComparison.OrdinalIgnoreCase))
        {
            SelectedKnowledgeBase = null;
            _session.AdvanceSelection();
            DocumentInventory = [];
        }

        await LoadKnowledgeBasesAsync();
        _session.CheckCurrent();
        NotifyStateChanged();
    }

    /// <summary>
    /// Opens the create knowledge-base dialog.
    /// </summary>
    public Task ShowCreateKnowledgeBaseDialogAsync()
        => _session.RunAsync(() => ShowCreateKnowledgeBaseDialogAsyncCore(), selectionBound: false);

    private async Task ShowCreateKnowledgeBaseDialogAsyncCore()
    {
        _session.CheckCurrent();
        var result = await _session.ShowAsync<CreateKnowledgeBaseDialog>(
            _localizer["RAG:Manage:CreateDialog:Title"],
            new DialogParameters(),
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        _session.CheckCurrent();

        if (result is not { Canceled: false, Data: KnowledgeBaseDialogResult data })
        {
            return;
        }

        var createResult = await _knowledgeBaseFacade.CreateAsync(data.Id, data.Name, data.Description);
        _session.CheckCurrent();

        var error = createResult;
        if (createResult.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["Common:Success"], Severity.Success);
        await LoadKnowledgeBasesAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Opens the edit knowledge-base dialog for the current selection.
    /// </summary>
    public Task ShowEditKnowledgeBaseDialogAsync()
        => _session.RunAsync(() => ShowEditKnowledgeBaseDialogAsyncCore(), selectionBound: true);

    private async Task ShowEditKnowledgeBaseDialogAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var parameters = new DialogParameters
        {
            { nameof(CreateKnowledgeBaseDialog.InitialId), originKnowledgeBase.Id },
            { nameof(CreateKnowledgeBaseDialog.InitialName), originKnowledgeBase.Name },
            { nameof(CreateKnowledgeBaseDialog.InitialDescription), originKnowledgeBase.Description },
            { nameof(CreateKnowledgeBaseDialog.SubmitText), _localizer["Common:Actions:Save"].Value },
            { nameof(CreateKnowledgeBaseDialog.IsIdReadOnly), true }
        };

        var result = await _session.ShowAsync<CreateKnowledgeBaseDialog>(
            _localizer["RAG:Manage:EditDialog:Title"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        _session.CheckCurrent();

        if (result is not { Canceled: false, Data: KnowledgeBaseDialogResult data })
        {
            return;
        }

        var updateResult = await _knowledgeBaseFacade.UpdateAsync(
                originKnowledgeBase.Id,
                data.Name,
                data.Description);
        _session.CheckCurrent();

        var error = updateResult;
        if (updateResult.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["Common:Success"], Severity.Success);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Opens the markdown document import dialog for the current selection.
    /// </summary>
    public Task ShowDocumentImportDialogAsync()
        => _session.RunAsync(() => ShowDocumentImportDialogAsyncCore(), selectionBound: true);

    private async Task ShowDocumentImportDialogAsyncCore()
    {
        _session.CheckCurrent();
        var originMarkdownGroup = SelectedMarkdownGroup;
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || string.IsNullOrEmpty(originMarkdownGroup))
        {
            return;
        }

        var documentsResult = await _knowledgeDocumentFacade.GetMarkdownDocumentsAsync(originMarkdownGroup);
        _session.CheckCurrent();

        var error = documentsResult.WithoutData();
        var documents = documentsResult.Data;
        if (documentsResult.IsFailure() || documents is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        var existingDocumentIds = DocumentInventory
            .Select(document => document.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parameters = new DialogParameters
        {
            { nameof(DocumentSelectionDialog.AvailableDocuments), documents.ToList() },
            { nameof(DocumentSelectionDialog.ExistingDocumentIds), existingDocumentIds }
        };

        var result = await _session.ShowAsync<DocumentSelectionDialog>(
            _localizer["KnowledgeBase:DocumentImport:Title"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        _session.CheckCurrent();

        if (result is not { Canceled: false, Data: List<string> selectedDocumentIds })
        {
            return;
        }

        var importResultEnvelope = await _knowledgeDocumentFacade.ImportMarkdownDocumentsAsync(
                originKnowledgeBase.Id,
                originMarkdownGroup,
                selectedDocumentIds);
        _session.CheckCurrent();

        var addError = importResultEnvelope.WithoutData();
        var importResult = importResultEnvelope.Data;
        if (importResultEnvelope.IsFailure() || importResult is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {addError.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(
            _localizer["KnowledgeBase:DocumentImport:Imported", importResult.AddedCount, importResult.SkippedCount],
            Severity.Success);
        await LoadDocumentInventoryAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Uploads one file as a pending knowledge-base document.
    /// </summary>
    public Task UploadFileAsync(IBrowserFile file)
        => _session.RunAsync(() => UploadFileAsyncCore(file), selectionBound: true);

    private async Task UploadFileAsyncCore(IBrowserFile file)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        try
        {
            const long maxFileSize = 10 * 1024 * 1024;
            if (file.Size > maxFileSize)
            {
                _snackbar.Add(_localizer["RAG:Indexing:FileTooLarge"], Severity.Error);
                return;
            }

            using var stream = file.OpenReadStream(maxFileSize, _session.Token);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(_session.Token);
            _session.CheckCurrent();

            var uploadResult = await _knowledgeDocumentFacade.UploadDocumentAsync(
                    originKnowledgeBase.Id,
                    file.Name,
                    content);
            _session.CheckCurrent();

            var error = uploadResult;
            if (uploadResult.IsFailure())
            {
                _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
                return;
            }

            _snackbar.Add(_localizer["Common:Success"], Severity.Success);
            await LoadDocumentInventoryAsync();
            _session.CheckCurrent();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {ex.Message}", Severity.Error);
        }
    }

    /// <summary>
    /// Deletes one document after confirmation.
    /// </summary>
    public Task DeleteDocumentAsync(DocumentQueueItem document)
        => _session.RunAsync(() => DeleteDocumentAsyncCore(document), selectionBound: true);

    private async Task DeleteDocumentAsyncCore(DocumentQueueItem document)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || !string.Equals(originKnowledgeBase.Id, document.KnowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;

        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["Common:Confirm"],
            _localizer["RAG:DocumentQueue:DeleteConfirm", document.Name],
            yesText: _localizer["Common:Delete"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        if (confirmed != true)
        {
            return;
        }

        var removeResult = await _knowledgeDocumentFacade.RemoveDocumentAsync(originKnowledgeBase.Id, document.Id);
        _session.CheckCurrent();

        var error = removeResult;
        if (removeResult.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["Common:Success"], Severity.Success);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentInventoryAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Opens the raw source preview for one knowledge-base document.
    /// </summary>
    public Task ShowDocumentPreviewAsync(DocumentQueueItem document)
        => _session.RunAsync(() => ShowDocumentPreviewAsyncCore(document), selectionBound: true);

    private async Task ShowDocumentPreviewAsyncCore(DocumentQueueItem document)
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null || !string.Equals(originKnowledgeBase.Id, document.KnowledgeBaseId, StringComparison.OrdinalIgnoreCase)) return;

        var result = await _knowledgeDocumentFacade.GetDocumentPreviewAsync(originKnowledgeBase.Id, document.Id);
        _session.CheckCurrent();
        var error = result.WithoutData();
        var preview = result.Data;
        if (result.IsFailure() || preview is null)
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        var parameters = new DialogParameters
        {
            { nameof(KnowledgeBaseDocumentPreviewDialog.DocumentName), preview.DocumentName },
            { nameof(KnowledgeBaseDocumentPreviewDialog.DocumentPath), preview.DocumentId },
            { nameof(KnowledgeBaseDocumentPreviewDialog.Content), preview.Content },
            { nameof(KnowledgeBaseDocumentPreviewDialog.SourceKind), preview.SourceKind },
            { nameof(KnowledgeBaseDocumentPreviewDialog.SourceGroupKey), preview.SourceGroupKey }
        };

        await _session.ShowAsync<KnowledgeBaseDocumentPreviewDialog>(
            _localizer["KnowledgeBase:Documents:Preview:Title"],
            parameters,
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        _session.CheckCurrent();
    }

    /// <summary>
    /// Clears all documents after confirmation.
    /// </summary>
    public Task ClearKnowledgeBaseDocumentsAsync()
        => _session.RunAsync(() => ClearKnowledgeBaseDocumentsAsyncCore(), selectionBound: true);

    private async Task ClearKnowledgeBaseDocumentsAsyncCore()
    {
        _session.CheckCurrent();
        var originKnowledgeBase = SelectedKnowledgeBase;
        if (originKnowledgeBase is null)
        {
            return;
        }

        var confirmed = await _session.ShowMessageBoxAsync(
            _localizer["Common:Confirm"],
            _localizer["RAG:DocumentQueue:ClearDocumentsConfirm", originKnowledgeBase.Name],
            yesText: _localizer["RAG:DocumentQueue:Actions:ClearDocuments"],
            cancelText: _localizer["Common:Cancel"]);
        _session.CheckCurrent();

        if (confirmed != true)
        {
            return;
        }

        var result = await _knowledgeDocumentFacade.ClearDocumentsAsync(originKnowledgeBase.Id);
        _session.CheckCurrent();
        var error = result.WithoutData();
        var removedCount = result.Data;
        if (result.IsFailure())
        {
            _snackbar.Add($"{_localizer["Common:Error"]}: {error.Message}", Severity.Error);
            return;
        }

        _snackbar.Add(_localizer["RAG:DocumentQueue:Messages:DocumentsCleared", removedCount], Severity.Success);
        await RefreshSelectedKnowledgeBaseAsync();
        _session.CheckCurrent();
        await LoadDocumentInventoryAsync();
        _session.CheckCurrent();
    }

    /// <summary>
    /// Shows the full indexing error for one document.
    /// </summary>
    public Task ShowIndexingErrorAsync(DocumentQueueItem document)
        => _session.RunAsync(() => ShowIndexingErrorAsyncCore(document), selectionBound: true);

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
