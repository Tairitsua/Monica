using Microsoft.Extensions.AI;
using Monica.AI.Abstractions;
using Monica.AI.Models;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.Facades;

/// <summary>
/// Host-facing facade for AI provider inspection and connectivity checks.
/// </summary>
public sealed class ProviderFacade(IAIProviderFactory providerFactory)
{
    /// <summary>Returns current provider metadata without acquiring network clients.</summary>
    public IReadOnlyList<AIProviderInfo> GetProviders()
    {
        return providerFactory.GetAllProviderInfos();
    }

    /// <summary>Runs the provider's small inference connectivity check under a captured configuration lease.</summary>
    public async Task<Result> TestProviderAsync(string providerId, CancellationToken ct = default)
    {
        using var lease = providerFactory.AcquireProvider(providerId);
        var provider = lease?.Provider;
        if (provider == null)
        {
            return Result.Failure($"Provider '{providerId}' not found");
        }

        try
        {
            await provider.TestConnectionAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(lease!.RedactDiagnostic(ex.GetMessageRecursively()));
        }
    }

    /// <summary>Sends a small inference request to one configured chat model.</summary>
    public async Task<Result> TestModelAsync(string providerId, string modelName, CancellationToken ct = default)
    {
        using var lease = providerFactory.AcquireProvider(providerId);
        var provider = lease?.Provider;
        if (provider == null)
        {
            return Result.Failure($"Provider '{providerId}' not found");
        }

        var model = provider.Info.SupportedModels?.FirstOrDefault(m =>
            string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        if (model is not LLMModelInfo)
        {
            return Result.Failure("Only LLM models are supported for testing");
        }

        try
        {
            var chatClient = provider.GetChatClient(modelName);
            await chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Hello")],
                new ChatOptions { MaxOutputTokens = 8 },
                ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Model test failed: {lease!.RedactDiagnostic(ex.GetMessageRecursively())}");
        }
    }

    /// <summary>Lists remote models without inference or automatic configuration changes.</summary>
    public async Task<Result<IReadOnlyList<AIRemoteModelInfo>>> FetchRemoteModelsAsync(
        string providerId, CancellationToken ct = default)
    {
        using var lease = providerFactory.AcquireProvider(providerId);
        var provider = lease?.Provider;
        if (provider == null)
        {
            return Result.Failure<IReadOnlyList<AIRemoteModelInfo>>($"Provider '{providerId}' not found");
        }

        if (!provider.SupportsRemoteModelListing)
        {
            return Result.Failure<IReadOnlyList<AIRemoteModelInfo>>("Provider does not support remote model listing");
        }

        try
        {
            var models = await provider.FetchRemoteModelsAsync(ct);
            return Result.Success(models);
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<AIRemoteModelInfo>>(lease!.RedactDiagnostic(ex.GetMessageRecursively()));
        }
    }

    /// <summary>Whether the configured protocol offers a remote model-list operation.</summary>
    public bool SupportsRemoteModelListing(string providerId)
    {
        var provider = providerFactory.GetProviderInfo(providerId);
        return provider?.SupportsRemoteModelListing ?? false;
    }

    /// <summary>
    /// Probes the actual vector dimensions of an embedding model by sending a short text
    /// and measuring the returned embedding length. Updates the model's Dimensions if successful.
    /// </summary>
    public async Task<Result<int>> ProbeEmbeddingDimensionsAsync(
        string providerId, string modelName, CancellationToken ct = default)
    {
        using var lease = providerFactory.AcquireProvider(providerId);
        var provider = lease?.Provider;
        if (provider == null)
            return Result.Failure<int>($"Provider '{providerId}' not found");

        var model = provider.Info.SupportedModels?.FirstOrDefault(m =>
            string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
        if (model is not EmbeddingModelInfo embeddingModel)
            return Result.Failure<int>("Only embedding models can be probed for dimensions");

        try
        {
            var generator = provider.GetEmbeddingGenerator(modelName);
            var result = await generator.GenerateAsync(["dimension probe"], cancellationToken: ct);
            var dimensions = result[0].Vector.Length;

            // Update the model's dimensions in place
            embeddingModel.Dimensions = dimensions;

            return Result.Success(dimensions);
        }
        catch (Exception ex)
        {
            return Result.Failure<int>($"Embedding probe failed: {lease!.RedactDiagnostic(ex.GetMessageRecursively())}");
        }
    }
}
