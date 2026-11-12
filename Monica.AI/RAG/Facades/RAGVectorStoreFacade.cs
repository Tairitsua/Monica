using Microsoft.Extensions.Logging;
using Monica.AI.RAG.Models;
using Monica.AI.RAG.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.RAG.Facades;

/// <summary>Host-facing vector-store lifecycle, diagnostics, and validation entry point.</summary>
public sealed class RAGVectorStoreFacade
{
    private readonly RAGVectorStoreService service;
    private readonly ILogger<RAGVectorStoreFacade> logger;

    internal RAGVectorStoreFacade(
        RAGVectorStoreService service,
        ILogger<RAGVectorStoreFacade> logger)
    {
        this.service = service;
        this.logger = logger;
    }

    public const string CAN_FORCE_REMOVE_RAG_SUPPORT_METADATA_KEY = "canForceRemoveRagSupport";

    /// <summary>Validates representative vector records for a knowledge base.</summary>
    public async Task<Result<KnowledgeBaseVectorValidationResult>> ValidateAsync(string knowledgeBaseId)
        => await ExecuteAsync(
            () => service.ValidateAsync(knowledgeBaseId),
            $"validate vectors for '{knowledgeBaseId}'");

    /// <summary>Returns vector-store diagnostics.</summary>
    public Result<VectorStoreDiagnosticInfo> GetDiagnostics()
    {
        try
        {
            return Result.Success(service.GetDiagnostics());
        }
        catch (Exception ex)
        {
            return Failure<VectorStoreDiagnosticInfo>(ex, "load vector-store diagnostics");
        }
    }

    /// <summary>Tests vector-store connectivity.</summary>
    public async Task<Result<VectorStoreConnectionTestResult>> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            return Result.Success(await service.TestConnectionAsync(ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vector-store connectivity test failed.");
            return Result.Success(new VectorStoreConnectionTestResult
            {
                Succeeded = false,
                ProbeCollectionName = string.Empty,
                Message = ex.GetMessageRecursively()
            });
        }
    }

    /// <summary>Returns vector collection existence status.</summary>
    public async Task<Result<KnowledgeBaseVectorCollectionStatus>> GetCollectionStatusAsync(string knowledgeBaseId)
        => await ExecuteAsync(
            () => service.GetCollectionStatusAsync(knowledgeBaseId),
            $"load vector collection status for '{knowledgeBaseId}'");

    /// <summary>Clears and resets a vector collection.</summary>
    public async Task<Result<KnowledgeBaseVectorCollectionOverwriteResult>> OverwriteCollectionAsync(
        string knowledgeBaseId)
        => await ExecuteAsync(
            () => service.OverwriteCollectionAsync(knowledgeBaseId),
            $"overwrite vector collection for '{knowledgeBaseId}'");

    /// <summary>Removes RAG support while preserving source documents.</summary>
    public async Task<Result<KnowledgeBaseRagSupportRemovalResult>> RemoveSupportAsync(
        string knowledgeBaseId,
        bool forceLocalMetadataRemoval = false)
    {
        try
        {
            return Result.Success(await service.RemoveSupportAsync(knowledgeBaseId, forceLocalMetadataRemoval));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove RAG support from '{KnowledgeBaseId}'.", knowledgeBaseId);
            if (RAGFailureTranslator.IsVectorStoreFailure(ex))
            {
                return Result.Failure<KnowledgeBaseRagSupportRemovalResult>(RAGFailureTranslator.DescribeRagSupportRemoval(ex))
                    .AppendMetadata(CAN_FORCE_REMOVE_RAG_SUPPORT_METADATA_KEY, true);
            }

            return Result.Failure<KnowledgeBaseRagSupportRemovalResult>(ex.GetMessageRecursively());
        }
    }

    private async Task<Result<T>> ExecuteAsync<T>(Func<Task<T>> action, string operation)
    {
        try
        {
            return Result.Success(await action());
        }
        catch (Exception ex)
        {
            return Failure<T>(ex, operation);
        }
    }

    private Result<T> Failure<T>(Exception exception, string operation)
    {
        logger.LogError(exception, "Failed to {Operation}.", operation);
        return Result.Failure<T>($"Failed to {operation}: {exception.GetMessageRecursively()}");
    }
}
