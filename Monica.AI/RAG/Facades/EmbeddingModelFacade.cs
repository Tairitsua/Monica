using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.AI.Abstractions;
using Monica.AI.KnowledgeBase.Abstractions;
using Monica.AI.Models;
using Monica.AI.RAG.Models;
using Monica.AI.RAG.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.RAG.Facades;

/// <summary>
/// Host-facing facade for embedding model discovery and knowledge-base binding.
/// </summary>
public class EmbeddingModelFacade(
    IServiceProvider serviceProvider,
    IAIProviderFactory providerFactory,
    IKnowledgeBaseStore knowledgeBaseStore,
    ILogger<EmbeddingModelFacade> logger)
{
    public Task<Result<IReadOnlyList<EmbeddingModelOption>>> GetEmbeddingModelsAsync()
    {
        try
        {
            var options = providerFactory.GetAllProviderInfos()
                .Where(provider => provider.IsValid)
                .SelectMany(provider => provider.SupportedModels?
                    .OfType<EmbeddingModelInfo>()
                    .Select(model => new EmbeddingModelOption
                    {
                        ProviderId = provider.ProviderId,
                        ProviderDisplayName = provider.DisplayName,
                        ModelName = model.ModelName,
                        Dimensions = model.Dimensions,
                        Description = model.Description
                    }) ?? [])
                .GroupBy(
                    option => EmbeddingModelOption.ToModelKey(option.ProviderId, option.ModelName),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(option => option.ProviderDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.ModelName, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

            return Task.FromResult(Result.Success<IReadOnlyList<EmbeddingModelOption>>(options));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get embedding model options.");
            return Task.FromResult<Result<IReadOnlyList<EmbeddingModelOption>>>(
                Result.Failure<IReadOnlyList<EmbeddingModelOption>>($"Failed to load embedding models: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets diagnostic metadata for every configured embedding model, including invalid providers.
    /// </summary>
    public Task<Result<IReadOnlyList<EmbeddingModelDiagnosticInfo>>> GetEmbeddingModelDiagnosticsAsync()
    {
        try
        {
            var diagnostics = providerFactory.GetAllProviderInfos()
                .SelectMany(provider => provider.SupportedModels?
                    .OfType<EmbeddingModelInfo>()
                    .Select(model => new EmbeddingModelDiagnosticInfo
                    {
                        ProviderId = provider.ProviderId,
                        ProviderDisplayName = provider.DisplayName,
                        ProviderStatus = provider.Status.ToString(),
                        IsProviderValid = provider.IsValid,
                        ModelName = model.ModelName,
                        Dimensions = model.Dimensions,
                        Description = model.Description,
                        ConfigurationErrors = provider.ConfigurationErrors ?? []
                    }) ?? [])
                .GroupBy(
                    option => EmbeddingModelOption.ToModelKey(option.ProviderId, option.ModelName),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(option => option.ProviderDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(option => option.ModelName, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();

            return Task.FromResult(Result.Success<IReadOnlyList<EmbeddingModelDiagnosticInfo>>(diagnostics));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get embedding model diagnostics.");
            return Task.FromResult<Result<IReadOnlyList<EmbeddingModelDiagnosticInfo>>>(
                Result.Failure<IReadOnlyList<EmbeddingModelDiagnosticInfo>>($"Failed to load embedding diagnostics: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Tests whether one configured embedding model can generate a vector through its provider.
    /// </summary>
    /// <param name="providerId">Provider identifier that owns the embedding model.</param>
    /// <param name="modelName">Embedding model name to test.</param>
    /// <param name="ct">Cancellation token used for the live embedding request.</param>
    public async Task<Result<EmbeddingModelConnectionTestResult>> TestEmbeddingModelConnectionAsync(
        string providerId,
        string modelName,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return Result.Failure<EmbeddingModelConnectionTestResult>("Embedding provider ID cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(modelName))
            {
                return Result.Failure<EmbeddingModelConnectionTestResult>("Embedding model name cannot be empty.");
            }

            using var lease = providerFactory.AcquireProvider(providerId);
            var provider = lease?.Provider;
            if (provider is null)
            {
                return Result.Failure<EmbeddingModelConnectionTestResult>($"Embedding provider '{providerId}' was not found.");
            }

            var embeddingModel = provider.Info.SupportedModels?
                .OfType<EmbeddingModelInfo>()
                .FirstOrDefault(model => string.Equals(model.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

            if (embeddingModel is null)
            {
                return Result.Failure<EmbeddingModelConnectionTestResult>($"Embedding model '{modelName}' is not configured on provider '{provider.ProviderId}'.");
            }

            var generator = provider.GetEmbeddingGenerator(embeddingModel.ModelName);
            var generated = await generator.GenerateAsync(["embedding connection test"], cancellationToken: ct);
            var embedding = generated.FirstOrDefault();
            if (embedding is null)
            {
                return Result.Failure<EmbeddingModelConnectionTestResult>("Embedding generator returned no vectors.");
            }

            var dimensions = embedding.Vector.Length;
            embeddingModel.Dimensions = dimensions;

            return Result.Success(new EmbeddingModelConnectionTestResult
            {
                ProviderId = provider.ProviderId,
                ModelName = embeddingModel.ModelName,
                Succeeded = true,
                Dimensions = dimensions,
                Message = $"Connection succeeded. Dimensions: {dimensions}."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to test embedding model '{ModelName}' on provider '{ProviderId}'.",
                modelName,
                providerId);
            return Result.Success(new EmbeddingModelConnectionTestResult
            {
                ProviderId = providerId,
                ModelName = modelName,
                Succeeded = false,
                Message = ex.GetMessageRecursively()
            });
        }
    }

    public async Task<Result<EmbeddingModelOption?>> GetKnowledgeBaseEmbeddingModelAsync(string kbId)
    {
        try
        {
            var kb = await knowledgeBaseStore.GetKnowledgeBaseAsync(kbId);
            if (kb is null)
            {
                return Result.Failure<EmbeddingModelOption?>("Knowledge base not found.");
            }

            if (string.IsNullOrWhiteSpace(kb.EmbeddingProviderId)
                || string.IsNullOrWhiteSpace(kb.EmbeddingModelName))
            {
                return Result.Success<EmbeddingModelOption?>(null);
            }

            var modelsResult = await GetEmbeddingModelsAsync();
            if (modelsResult.IsFailure())
            {
                return Result.FromFailure<EmbeddingModelOption?>(modelsResult);
            }

            var models = modelsResult.Data
                ?? throw new InvalidOperationException("Embedding model discovery returned no data.");

            var model = models.FirstOrDefault(m =>
                string.Equals(m.ProviderId, kb.EmbeddingProviderId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.ModelName, kb.EmbeddingModelName, StringComparison.OrdinalIgnoreCase));

            if (model is not null)
            {
                return Result.Success<EmbeddingModelOption?>(model);
            }

            var provider = providerFactory.GetProviderInfo(kb.EmbeddingProviderId);
            var fallbackProviderId = kb.EmbeddingProviderId.Trim();

            return Result.Success<EmbeddingModelOption?>(new EmbeddingModelOption
            {
                ProviderId = kb.EmbeddingProviderId,
                ProviderDisplayName = provider?.DisplayName ?? fallbackProviderId,
                ModelName = kb.EmbeddingModelName,
                Description = "Embedding model is persisted in KB metadata but unavailable in current providers."
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get embedding model for KB '{KbId}'", kbId);
            return Result.Failure<EmbeddingModelOption?>($"Failed to get embedding model: {ex.GetMessageRecursively()}");
        }
    }

    public async Task<Result> SetKnowledgeBaseEmbeddingModelAsync(
        string kbId,
        string providerId,
        string modelName,
        bool clearIndex = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return Result.Failure("Embedding provider ID cannot be empty.");
            }

            if (string.IsNullOrWhiteSpace(modelName))
            {
                return Result.Failure("Embedding model name cannot be empty.");
            }

            var modelsResult = await GetEmbeddingModelsAsync();
            if (modelsResult.IsFailure())
            {
                return Result.FromFailure(modelsResult);
            }

            var models = modelsResult.Data
                ?? throw new InvalidOperationException("Embedding model discovery returned no data.");

            var exists = models.Any(option =>
                string.Equals(option.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(option.ModelName, modelName, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                return Result.Failure(
                    $"Embedding model '{modelName}' is not available on provider '{providerId}'.");
            }

            await GetVectorStoreService().SetEmbeddingModelAsync(
                kbId,
                providerId,
                modelName,
                clearIndex);

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to set embedding binding for KB '{KbId}' to provider '{ProviderId}' model '{ModelName}'",
                kbId, providerId, modelName);
            return ex is InvalidOperationException or KeyNotFoundException
                ? Result.Failure(ex.GetMessageRecursively())
                : Result.Failure($"Failed to set embedding model: {ex.GetMessageRecursively()}");
        }
    }

    // Delay vector pipeline resolution so the page can load provider metadata
    // without constructing the vector-store pipeline on entry.
    private RAGVectorStoreService GetVectorStoreService()
        => serviceProvider.GetRequiredService<RAGVectorStoreService>();
}
