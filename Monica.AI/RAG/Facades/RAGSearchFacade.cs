using Microsoft.Extensions.Logging;
using Monica.AI.RAG.Models;
using Monica.AI.RAG.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.RAG.Facades;

/// <summary>Host-facing semantic retrieval entry point.</summary>
public sealed class RAGSearchFacade
{
    private readonly RAGSearchService service;
    private readonly ILogger<RAGSearchFacade> logger;

    internal RAGSearchFacade(RAGSearchService service, ILogger<RAGSearchFacade> logger)
    {
        this.service = service;
        this.logger = logger;
    }

    /// <summary>Searches selected knowledge bases.</summary>
    public async Task<Result<IReadOnlyList<TextSearchResult>>> SearchAsync(
        string query,
        IEnumerable<string> knowledgeBaseIds,
        int topK = 5,
        RAGSearchEmbeddingOverride? embeddingOverride = null,
        CancellationToken ct = default)
    {
        try
        {
            return Result.Success(await service.SearchAsync(query, knowledgeBaseIds, topK, embeddingOverride, ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Semantic search failed for '{Query}'.", query);
            return Result.Failure<IReadOnlyList<TextSearchResult>>($"Search failed: {ex.GetMessageRecursively()}");
        }
    }
}
