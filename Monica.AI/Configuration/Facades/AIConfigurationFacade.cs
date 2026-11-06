using Monica.AI.Abstractions;
using Monica.AI.Configuration.Models;
using Monica.AI.Configuration.Services;
using Monica.AI.Services;
using Monica.AI.Models;
using Monica.AI.Providers;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.Configuration.Facades;

/// <summary>
/// Operator-facing provider/model management. Hosts must apply their operational authorization policy before
/// exposing these methods. Keys are write-only, protected by the host key ring, and absent from returned snapshots.
/// </summary>
public sealed class AIConfigurationFacade
{
    private readonly AIConfigurationService _configuration;
    private readonly IAIProviderFactory _providers;
    private readonly AIModelCatalog _catalog;

    internal AIConfigurationFacade(AIConfigurationService configuration, IAIProviderFactory providers, AIModelCatalog catalog)
    {
        _configuration = configuration;
        _providers = providers;
        _catalog = catalog;
    }

    /// <summary>Reloads host settings and returns their current revision and credential-free effective values.</summary>
    public Task<Result<AIConfigurationSnapshot>> GetAsync(CancellationToken ct = default) =>
        ExecuteAsync(() => _configuration.RefreshAsync(ct));

    /// <summary>
    /// Creates or replaces a persisted provider override at the supplied revision. A null or empty API key keeps
    /// the existing key; <paramref name="clearApiKey"/> explicitly removes it. New requests see saved changes.
    /// </summary>
    public Task<Result<AIConfigurationSnapshot>> UpsertProviderAsync(
        AIProviderConfiguration configuration,
        string? apiKey,
        bool clearApiKey,
        long expectedRevision,
        CancellationToken ct = default) =>
        ExecuteAsync(() => _configuration.UpsertAsync(configuration, apiKey, clearApiKey, expectedRevision, ct));

    /// <summary>Deletes a runtime-created provider. Code-defined providers can be disabled but not deleted.</summary>
    public Task<Result<AIConfigurationSnapshot>> RemoveProviderAsync(string providerId, long expectedRevision, CancellationToken ct = default) =>
        ExecuteAsync(() => _configuration.RemoveAsync(providerId, expectedRevision, ct));

    /// <summary>Removes a persisted override and restores all code-defined values, including the code credential.</summary>
    public Task<Result<AIConfigurationSnapshot>> ResetProviderAsync(string providerId, long expectedRevision, CancellationToken ct = default) =>
        ExecuteAsync(() => _configuration.ResetAsync(providerId, expectedRevision, ct));

    /// <summary>
    /// Lists remote models without saving them or probing inference capabilities. Explicitly configured metadata
    /// wins; catalog metadata applies only to official protocol endpoints. Custom aliases remain unknown.
    /// </summary>
    public async Task<Result<IReadOnlyList<AIModelConfiguration>>> DiscoverModelsAsync(string providerId, CancellationToken ct = default)
    {
        IAIProviderLease? lease = null;
        try
        {
            lease = _providers.AcquireProvider(providerId)
                ?? throw new KeyNotFoundException($"Provider '{providerId}' was not found.");
            var setting = _configuration.GetSnapshot().Providers.FirstOrDefault(entry =>
                string.Equals(entry.Configuration.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("This custom provider does not support runtime configuration.");
            var remote = await lease.Provider.FetchRemoteModelsAsync(ct).ConfigureAwait(false);
            return Result.Success(MergeDiscoveredModels(setting.Configuration, remote));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result.Failure<IReadOnlyList<AIModelConfiguration>>(Redact(ex, lease)); }
        finally { lease?.Dispose(); }
    }

    /// <summary>
    /// Lists models using unsaved connection settings without publishing a provider or writing configuration.
    /// A supplied API key is used only for this request. If it is empty, <paramref name="useSavedApiKey"/>
    /// explicitly allows the matching saved provider's credential. The transient provider is always disposed.
    /// Cancellation propagates; other failures are returned with captured credentials redacted.
    /// </summary>
    public async Task<Result<IReadOnlyList<AIModelConfiguration>>> DiscoverDraftModelsAsync(
        AIProviderConfiguration configuration, string? apiKey = null, bool useSavedApiKey = false, CancellationToken ct = default)
    {
        var capturedKey = apiKey;
        try
        {
            ct.ThrowIfCancellationRequested();
            var draft = _configuration.ResolveDraft(configuration, apiKey, useSavedApiKey);
            capturedKey = draft.ApiKey;
            using var provider = AIConfiguredProviderFactory.Create(
                AIConfiguredProviderFactory.CreateOptions(draft.Configuration, draft.ApiKey), _catalog);
            var remote = await provider.FetchRemoteModelsAsync(ct).ConfigureAwait(false);
            return Result.Success(MergeDiscoveredModels(configuration, remote));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = _configuration.Redact(ex.GetMessageRecursively());
            return Result.Failure<IReadOnlyList<AIModelConfiguration>>(string.IsNullOrEmpty(capturedKey) ? message : message.Replace(capturedKey, "[redacted]", StringComparison.Ordinal));
        }
    }

    private IReadOnlyList<AIModelConfiguration> MergeDiscoveredModels(AIProviderConfiguration configuration, IReadOnlyList<AIRemoteModelInfo> remote)
    {
        var configured = configuration.Models.DistinctBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static model => model.ModelName, StringComparer.OrdinalIgnoreCase);
        var trustCatalog = AIProviderMetadataPolicy.AllowsBuiltInTemplates(configuration.ProviderType, configuration.BaseUrl);
        return remote.Select(model =>
            {
                var evidence = model.Configuration ?? new AIModelConfiguration
                {
                    ModelName = model.ModelId, DisplayName = model.Metadata?.GetValueOrDefault("DisplayName")
                };
                if (trustCatalog && _catalog.GetModel(model.ModelId) is { } known)
                    evidence = evidence.WithDiscoveredMetadata(AIModelConfiguration.FromModelInfo(known));
                return configured.TryGetValue(model.ModelId, out var existing)
                    ? existing.WithDiscoveredMetadata(evidence) : evidence;
            })
            .DistinctBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Tests authentication and connectivity using model discovery, without inference billing. Models unsupported
    /// by discovery can be tested explicitly with <see cref="TestModelAsync"/> after configuration.
    /// </summary>
    public async Task<Result> TestProviderAsync(string providerId, CancellationToken ct = default)
    {
        IAIProviderLease? lease = null;
        try
        {
            lease = _providers.AcquireProvider(providerId)
                ?? throw new KeyNotFoundException($"Provider '{providerId}' was not found.");
            await lease.Provider.FetchRemoteModelsAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result.Failure(Redact(ex, lease)); }
        finally { lease?.Dispose(); }
    }

    /// <summary>Sends a deliberately small text request to verify one configured model; this may incur usage charges.</summary>
    public async Task<Result> TestModelAsync(string providerId, string modelName, CancellationToken ct = default)
    {
        IAIProviderLease? lease = null;
        try
        {
            lease = _providers.AcquireProvider(providerId)
                ?? throw new KeyNotFoundException($"Provider '{providerId}' was not found.");
            var client = lease.Provider.GetChatClient(modelName);
            await client.GetResponseAsync(
                [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "Reply with OK.")],
                new Microsoft.Extensions.AI.ChatOptions { MaxOutputTokens = 16 }, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Result.Failure(Redact(ex, lease)); }
        finally { lease?.Dispose(); }
    }

    private string Redact(Exception exception, IAIProviderLease? lease)
    {
        var diagnostic = exception.GetMessageRecursively();
        return _configuration.Redact(lease?.RedactDiagnostic(diagnostic) ?? diagnostic);
    }

    private async Task<Result<T>> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try { return Result.Success(await operation().ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result.Failure<T>(_configuration.Redact(ex.GetMessageRecursively())); }
    }

}
