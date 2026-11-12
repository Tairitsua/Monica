using Microsoft.Extensions.Logging;
using Monica.AI.KnowledgeBase.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.KnowledgeBase.Facades;

/// <summary>
/// Host-facing entry point for knowledge-base metadata and lifecycle operations.
/// </summary>
public sealed class KnowledgeBaseFacade
{
    private readonly KnowledgeBaseService service;
    private readonly ILogger<KnowledgeBaseFacade> logger;

    internal KnowledgeBaseFacade(
        KnowledgeBaseService service,
        ILogger<KnowledgeBaseFacade> logger)
    {
        this.service = service;
        this.logger = logger;
    }

    /// <summary>
    /// Returns all knowledge bases.
    /// </summary>
    public async Task<Result<IReadOnlyList<Models.KnowledgeBase>>> GetAllAsync()
        => await ExecuteAsync(() => service.GetAllAsync(), "load knowledge bases");

    /// <summary>
    /// Returns one knowledge base by id.
    /// </summary>
    public async Task<Result<Models.KnowledgeBase>> GetByIdAsync(string id)
    {
        try
        {
            var knowledgeBase = await service.GetByIdAsync(id);
            return knowledgeBase is null
                ? Result.Failure<Models.KnowledgeBase>($"Knowledge base '{id}' was not found.")
                : Result.Success(knowledgeBase);
        }
        catch (Exception ex)
        {
            return Failure<Models.KnowledgeBase>(ex, $"load knowledge base '{id}'");
        }
    }

    /// <summary>
    /// Creates a knowledge base.
    /// </summary>
    public async Task<Result<Models.KnowledgeBase>> CreateAsync(
        string id,
        string name,
        string? description = null)
    {
        try
        {
            return Result.Success(await service.CreateAsync(id, name, description));
        }
        catch (Exception ex)
        {
            return Failure<Models.KnowledgeBase>(ex, $"create knowledge base '{id}'");
        }
    }

    /// <summary>
    /// Updates a knowledge base.
    /// </summary>
    public async Task<Result<Models.KnowledgeBase>> UpdateAsync(
        string id,
        string name,
        string? description = null)
    {
        try
        {
            return Result.Success(await service.UpdateAsync(id, name, description));
        }
        catch (Exception ex)
        {
            return Failure<Models.KnowledgeBase>(ex, $"update knowledge base '{id}'");
        }
    }

    /// <summary>
    /// Deletes a knowledge base and all resources owned by participating optional features.
    /// </summary>
    public async Task<Result> DeleteAsync(string id)
    {
        try
        {
            await service.DeleteAsync(id);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete knowledge base '{KnowledgeBaseId}'.", id);
            return Result.Failure($"Failed to delete knowledge base: {ex.GetMessageRecursively()}");
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
