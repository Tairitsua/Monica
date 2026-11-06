using Microsoft.Extensions.Logging;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.KnowledgeBase.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Markdown.Abstractions;
using Monica.Markdown.Models;

namespace Monica.AI.KnowledgeBase.Facades;

/// <summary>
/// Host-facing entry point for document import, inventory, preview, and removal operations.
/// </summary>
public sealed class KnowledgeDocumentFacade
{
    private readonly KnowledgeDocumentService service;
    private readonly IMarkdownDocumentCatalog markdownCatalog;
    private readonly ILogger<KnowledgeDocumentFacade> logger;

    internal KnowledgeDocumentFacade(
        KnowledgeDocumentService service,
        IMarkdownDocumentCatalog markdownCatalog,
        ILogger<KnowledgeDocumentFacade> logger)
    {
        this.service = service;
        this.markdownCatalog = markdownCatalog;
        this.logger = logger;
    }

    /// <summary>
    /// Returns markdown groups available for import.
    /// </summary>
    public async Task<Result<List<MarkdownDocumentGroup>>> GetMarkdownGroupsAsync()
    {
        try
        {
            return Result.Success(await markdownCatalog.GetAllDocumentGroupsAsync());
        }
        catch (Exception ex)
        {
            return Failure<List<MarkdownDocumentGroup>>(ex, "load markdown groups");
        }
    }

    /// <summary>
    /// Returns markdown documents in one import group.
    /// </summary>
    public async Task<Result<IReadOnlyList<MarkdownDocument>>> GetMarkdownDocumentsAsync(string groupKey)
    {
        try
        {
            return Result.Success<IReadOnlyList<MarkdownDocument>>(await markdownCatalog.GetDocumentsAsync(groupKey));
        }
        catch (Exception ex)
        {
            return Failure<IReadOnlyList<MarkdownDocument>>(ex, $"load markdown group '{groupKey}'");
        }
    }

    /// <summary>
    /// Imports selected markdown documents as pending inventory items.
    /// </summary>
    public async Task<Result<KnowledgeBaseDocumentImportResult>> ImportMarkdownDocumentsAsync(
        string knowledgeBaseId,
        string groupKey,
        IEnumerable<string> documentIds)
    {
        try
        {
            return Result.Success(await service.ImportMarkdownAsync(knowledgeBaseId, groupKey, documentIds));
        }
        catch (Exception ex)
        {
            return Failure<KnowledgeBaseDocumentImportResult>(ex, $"import documents into '{knowledgeBaseId}'");
        }
    }

    /// <summary>
    /// Uploads one source document.
    /// </summary>
    public async Task<Result> UploadDocumentAsync(string knowledgeBaseId, string fileName, string content)
    {
        try
        {
            await service.UploadAsync(knowledgeBaseId, fileName, content);
            return Result.Success().WithMessage($"Uploaded '{fileName}' successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upload document '{FileName}'.", fileName);
            return Result.Failure($"Failed to upload document: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Returns a knowledge base's document inventory.
    /// </summary>
    public async Task<Result<IReadOnlyList<DocumentQueueItem>>> GetDocumentInventoryAsync(string knowledgeBaseId)
    {
        try
        {
            return Result.Success(await service.GetInventoryAsync(knowledgeBaseId));
        }
        catch (Exception ex)
        {
            return Failure<IReadOnlyList<DocumentQueueItem>>(ex, $"load inventory for '{knowledgeBaseId}'");
        }
    }

    /// <summary>
    /// Returns source content for one document.
    /// </summary>
    public async Task<Result<KnowledgeBaseDocumentPreview>> GetDocumentPreviewAsync(
        string knowledgeBaseId,
        string documentId)
    {
        try
        {
            var preview = await service.GetPreviewAsync(knowledgeBaseId, documentId);
            return preview is null
                ? Result.Failure<KnowledgeBaseDocumentPreview>($"Source content for document '{documentId}' was not found.")
                : Result.Success(preview);
        }
        catch (Exception ex)
        {
            return Failure<KnowledgeBaseDocumentPreview>(ex, $"load document '{documentId}'");
        }
    }

    /// <summary>
    /// Removes one document and resources owned by optional features.
    /// </summary>
    public async Task<Result> RemoveDocumentAsync(string knowledgeBaseId, string documentId)
    {
        try
        {
            await service.RemoveAsync(knowledgeBaseId, documentId);
            return Result.Success().WithMessage("Document removed successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove document '{DocumentId}'.", documentId);
            return Result.Failure($"Failed to remove document: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Clears all documents and resources owned by optional features.
    /// </summary>
    public async Task<Result<int>> ClearDocumentsAsync(string knowledgeBaseId)
    {
        try
        {
            return Result.Success(await service.ClearAsync(knowledgeBaseId));
        }
        catch (Exception ex)
        {
            return Failure<int>(ex, $"clear documents for '{knowledgeBaseId}'");
        }
    }

    private Result<T> Failure<T>(Exception exception, string operation)
    {
        logger.LogError(exception, "Failed to {Operation}.", operation);
        return Result.Failure<T>($"Failed to {operation}: {exception.GetMessageRecursively()}");
    }
}
