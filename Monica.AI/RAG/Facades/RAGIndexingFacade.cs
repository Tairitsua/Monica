using Microsoft.Extensions.Logging;
using Monica.AI.KnowledgeBase.Models;
using Monica.AI.RAG.Models;
using Monica.AI.RAG.Services;
using Monica.AI.RAG.Services.Support;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.RAG.Facades;

/// <summary>Host-facing document queue, indexing operation, and chunk-view entry point.</summary>
public sealed class RAGIndexingFacade
{
    private readonly RAGDocumentService documentService;
    private readonly BatchIndexCoordinator batchCoordinator;
    private readonly ChunkViewCoordinator chunkViewCoordinator;
    private readonly ILogger<RAGIndexingFacade> logger;

    internal RAGIndexingFacade(
        RAGDocumentService documentService,
        BatchIndexCoordinator batchCoordinator,
        ChunkViewCoordinator chunkViewCoordinator,
        ILogger<RAGIndexingFacade> logger)
    {
        this.documentService = documentService;
        this.batchCoordinator = batchCoordinator;
        this.chunkViewCoordinator = chunkViewCoordinator;
        this.logger = logger;
    }

    /// <summary>Adds markdown documents to the indexing queue.</summary>
    public async Task<Result<int>> AddDocumentsAsync(
        string knowledgeBaseId,
        IEnumerable<string> documentIds,
        string? sourceGroupKey = null)
        => await ExecuteAsync(
            () => documentService.AddToQueueAsync(
                knowledgeBaseId,
                documentIds,
                KnowledgeDocumentSourceKinds.Markdown,
                sourceGroupKey),
            $"add documents to '{knowledgeBaseId}'");

    /// <summary>Clears pending and failed queue entries.</summary>
    public async Task<Result<int>> ClearPendingQueueAsync(string knowledgeBaseId)
        => await ExecuteAsync(
            () => documentService.ClearPendingQueueAsync(knowledgeBaseId),
            $"clear pending queue for '{knowledgeBaseId}'");

    /// <summary>Returns one document to pending state after removing current vectors.</summary>
    public async Task<Result> QueueDocumentForReindexAsync(string knowledgeBaseId, string documentId)
    {
        try
        {
            await documentService.QueueForReindexAsync(knowledgeBaseId, documentId);
            return Result.Success().WithMessage("Document queued for reindexing.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to queue '{DocumentId}' for reindexing.", documentId);
            return Result.Failure($"Failed to queue document: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>Returns every document in a knowledge base to pending state.</summary>
    public async Task<Result<int>> QueueKnowledgeBaseForReindexAsync(string knowledgeBaseId)
        => await ExecuteAsync(
            () => documentService.QueueKnowledgeBaseForReindexAsync(knowledgeBaseId),
            $"queue '{knowledgeBaseId}' for reindexing");

    /// <summary>Indexes one queued document.</summary>
    public async Task<Result> IndexDocumentAsync(
        string knowledgeBaseId,
        string documentId,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await batchCoordinator.StartDocumentIndexingAsync(knowledgeBaseId, documentId, progress, ct);
            return Result.Success().WithMessage("Document indexed successfully.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to index '{DocumentId}'.", documentId);
            return Result.Failure($"Failed to index document: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>Starts a tracked batch indexing operation.</summary>
    public async Task<Result> StartBatchAsync(
        string knowledgeBaseId,
        IEnumerable<string>? documentIds = null,
        int maxConcurrency = 5,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken ct = default)
    {
        var outcome = await batchCoordinator.StartBatchIndexingAsync(
            knowledgeBaseId,
            maxConcurrency,
            progress,
            ct,
            documentIds);
        return outcome.Succeeded ? Result.Success().WithMessage(outcome.Message) : Result.Failure(outcome.Message);
    }

    /// <summary>Requests cancellation of an active batch operation.</summary>
    public async Task<Result> CancelBatchAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        try
        {
            return Result.Success().WithMessage(await batchCoordinator.CancelBatchIndexingAsync(knowledgeBaseId, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel batch indexing for '{KnowledgeBaseId}'.", knowledgeBaseId);
            return Result.Failure($"Failed to cancel indexing: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>Returns whether a batch operation is active.</summary>
    public bool IsBatchActive(string knowledgeBaseId) => batchCoordinator.IsBatchIndexingActive(knowledgeBaseId);

    /// <summary>Returns original source text and chunk highlights.</summary>
    public async Task<Result<DocumentChunkView>> GetDocumentChunksAsync(
        string knowledgeBaseId,
        string documentId)
        => await ExecuteAsync(
            () => chunkViewCoordinator.GetDocumentChunksAsync(knowledgeBaseId, documentId),
            $"load chunks for '{documentId}'");

    private async Task<Result<T>> ExecuteAsync<T>(Func<Task<T>> action, string operation)
    {
        try
        {
            return Result.Success(await action());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to {Operation}.", operation);
            return Result.Failure<T>($"Failed to {operation}: {ex.GetMessageRecursively()}");
        }
    }
}
