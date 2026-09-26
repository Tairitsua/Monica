using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Monica.AI.Abstractions;
using Monica.AI.Configuration.Abstractions;
using Monica.AI.Configuration.Models;
using Monica.AI.Providers;
using Monica.AI.Services;
using Monica.Modules;

namespace Monica.AI.Configuration.Services;

/// <summary>Owns settings precedence, credential protection, validation, and revision-checked mutations.</summary>
internal sealed class AIConfigurationService(
    IAIConfigurationStore store,
    IEnumerable<AIProviderDefinition> definitions,
    AIModelCatalog catalog,
    IDataProtectionProvider dataProtection,
    IOptions<ModuleAIOption> moduleOptions,
    IEnumerable<IAIProvider>? customProviders = null)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NO_VALIDATION_ERRORS =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly AIConfigurationValidationMode _validationMode = moduleOptions.Value.ConfigurationValidationMode;
    private readonly ValidatedDefaults _defaults = LoadDefaults(definitions, catalog, moduleOptions.Value.ConfigurationValidationMode);
    private readonly IReadOnlyList<IAIProvider> _customProviders = customProviders?.ToArray() ?? [];
    private ValidatedSettings _settings = LoadSettings(store, moduleOptions.Value.ConfigurationValidationMode);

    public long CurrentRevision => Volatile.Read(ref _settings).Document.Revision;

    public string Redact(string message)
    {
        foreach (var key in Resolve().Providers.Select(static provider => provider.ApiKey).Where(static key => !string.IsNullOrWhiteSpace(key)))
        {
            message = message.Replace(key!, "[redacted]", StringComparison.Ordinal);
        }

        return message;
    }

    public AIConfigurationSnapshot GetSnapshot() => ProjectSnapshot(Volatile.Read(ref _settings));

    public async Task<AIConfigurationSnapshot> RefreshAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var latest = LoadSettings(store, _validationMode);
            Volatile.Write(ref _settings, latest);
            return ProjectSnapshot(latest);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public EffectiveAIConfiguration Resolve()
    {
        var settings = Volatile.Read(ref _settings);
        var overrides = settings.Document.Providers.ToDictionary(static entry => entry.Configuration.ProviderId, StringComparer.OrdinalIgnoreCase);
        var ids = _defaults.Providers.Keys.Concat(overrides.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var providers = new List<ResolvedAIProvider>();
        foreach (var id in ids)
        {
            _defaults.Providers.TryGetValue(id, out var baseline);
            overrides.TryGetValue(id, out var persisted);
            var configuration = persisted?.Configuration ?? baseline!.Configuration;
            string? apiKey;
            string? credentialError = null;
            try
            {
                apiKey = persisted?.ProtectedApiKey is { } protectedKey
                    ? GetProtector(id).Unprotect(protectedKey)
                    : persisted is null || persisted.UseCodeApiKey ? baseline?.ApiKey : null;
            }
            catch (CryptographicException)
            {
                apiKey = null;
                credentialError = "The stored API key cannot be decrypted by this host. Restore the data-protection key ring or enter the API key again.";
            }

            // A persisted override replaces a code default entirely, so its own validation outcome applies.
            var validationErrors = persisted is not null
                ? settings.ValidationErrors.GetValueOrDefault(id, [])
                : _defaults.ValidationErrors.GetValueOrDefault(id, []);
            providers.Add(new ResolvedAIProvider(configuration, apiKey, credentialError, validationErrors));
        }

        return new EffectiveAIConfiguration(settings.Document.Revision, providers);
    }

    internal ResolvedAIProvider ResolveDraft(AIProviderConfiguration draft, string? apiKey, bool useSavedApiKey)
    {
        ArgumentNullException.ThrowIfNull(draft);
        // Discovery needs connection settings only; unfinished model edits must not block listing models.
        var connection = Normalize(draft with { ProviderId = string.IsNullOrWhiteSpace(draft.ProviderId) ? "draft" : draft.ProviderId,
            Models = [], DefaultModel = null });
        ValidateConfiguration(connection);
        if (string.IsNullOrEmpty(apiKey) && useSavedApiKey)
        {
            var existing = Resolve().Providers.FirstOrDefault(provider => SameId(provider.Configuration.ProviderId, draft.ProviderId))
                ?? throw new KeyNotFoundException("The saved provider credential was not found.");
            if (existing.CredentialError is { } error) throw new InvalidOperationException(error);
            apiKey = existing.ApiKey;
        }
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Enter an API key before fetching available models.");
        return new ResolvedAIProvider(connection, apiKey, null, []);
    }

    public Task<AIConfigurationSnapshot> UpsertAsync(
        AIProviderConfiguration configuration,
        string? apiKey,
        bool clearApiKey,
        long expectedRevision,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalized = Normalize(configuration);
        ValidateConfiguration(normalized);
        if (_customProviders.Any(provider => SameId(provider.ProviderId, normalized.ProviderId)))
        {
            throw new InvalidOperationException("This identifier belongs to a host-supplied custom provider and cannot be replaced by runtime settings.");
        }

        if (normalized.IsDefault && _customProviders.Any(static provider => provider.Info.IsValid && provider.Info.IsDefault))
        {
            throw new InvalidOperationException("The host has a custom default provider. Clear its code-defined default before choosing a runtime default.");
        }
        if (clearApiKey && !string.IsNullOrEmpty(apiKey))
        {
            throw new ArgumentException("An API key cannot be supplied and cleared in the same operation.");
        }

        return MutateAsync(expectedRevision, entries =>
        {
            var existing = entries.FirstOrDefault(entry => SameId(entry.Configuration.ProviderId, normalized.ProviderId));
            var replacement = new AIPersistedProvider
            {
                Configuration = normalized,
                ProtectedApiKey = clearApiKey ? null : !string.IsNullOrEmpty(apiKey)
                    ? GetProtector(normalized.ProviderId).Protect(apiKey)
                    : existing?.ProtectedApiKey,
                UseCodeApiKey = !clearApiKey && string.IsNullOrEmpty(apiKey)
                    && (existing?.UseCodeApiKey ?? _defaults.Providers.ContainsKey(normalized.ProviderId))
            };
            entries.RemoveAll(entry => SameId(entry.Configuration.ProviderId, normalized.ProviderId));
            if (normalized.IsDefault)
            {
                // Choosing a new default is one atomic settings mutation, including inherited code defaults.
                foreach (var current in ProjectSnapshot(new ValidatedSettings(new AIConfigurationDocument { Providers = entries.ToArray() }, NO_VALIDATION_ERRORS)).Providers)
                {
                    if (!current.Configuration.IsDefault || SameId(current.Configuration.ProviderId, normalized.ProviderId))
                    {
                        continue;
                    }

                    var old = entries.FirstOrDefault(entry => SameId(entry.Configuration.ProviderId, current.Configuration.ProviderId));
                    entries.RemoveAll(entry => SameId(entry.Configuration.ProviderId, current.Configuration.ProviderId));
                    entries.Add((old ?? new AIPersistedProvider
                    {
                        Configuration = current.Configuration, UseCodeApiKey = current.IsCodeDefined
                    }) with { Configuration = current.Configuration with { IsDefault = false } });
                }
            }

            entries.Add(replacement);
        }, ct);
    }

    public Task<AIConfigurationSnapshot> RemoveAsync(string providerId, long expectedRevision, CancellationToken ct)
    {
        if (_defaults.Providers.ContainsKey(providerId))
        {
            throw new InvalidOperationException("Code-defined providers cannot be deleted. Disable the provider or reset its override.");
        }

        return MutateAsync(expectedRevision, entries =>
        {
            if (entries.RemoveAll(entry => SameId(entry.Configuration.ProviderId, providerId)) == 0)
            {
                throw new KeyNotFoundException($"Provider '{providerId}' does not exist.");
            }
        }, ct);
    }

    public Task<AIConfigurationSnapshot> ResetAsync(string providerId, long expectedRevision, CancellationToken ct)
    {
        if (!_defaults.Providers.ContainsKey(providerId))
        {
            throw new InvalidOperationException("Only code-defined providers have a baseline to restore.");
        }

        return MutateAsync(expectedRevision, entries =>
        {
            entries.RemoveAll(entry => SameId(entry.Configuration.ProviderId, providerId));
            EnsureOneDefault(entries);
        }, ct);
    }

    private async Task<AIConfigurationSnapshot> MutateAsync(
        long expectedRevision,
        Action<List<AIPersistedProvider>> mutation,
        CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = Volatile.Read(ref _settings).Document;
            if (current.Revision != expectedRevision)
            {
                throw new AIConfigurationConflictException(expectedRevision, current.Revision);
            }

            var entries = current.Providers.ToList();
            mutation(entries);
            var committed = await store.WriteAsync(new AIConfigurationDocument { Providers = entries.ToArray() }, expectedRevision, ct)
                .ConfigureAwait(false);
            var validated = ValidateSettings(committed, _validationMode);
            Volatile.Write(ref _settings, validated);
            return ProjectSnapshot(validated);
        }
        catch (AIConfigurationConflictException)
        {
            Volatile.Write(ref _settings, LoadSettings(store, _validationMode));
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private AIConfigurationSnapshot ProjectSnapshot(ValidatedSettings settings)
    {
        var providers = _defaults.Providers.ToDictionary(static entry => entry.Key, entry => new AIProviderSettings
        {
            Configuration = Clone(entry.Value.Configuration), HasApiKey = !string.IsNullOrWhiteSpace(entry.Value.ApiKey), IsCodeDefined = true,
            ValidationErrors = _defaults.ValidationErrors.GetValueOrDefault(entry.Key, [])
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in settings.Document.Providers)
        {
            _defaults.Providers.TryGetValue(entry.Configuration.ProviderId, out var baseline);
            providers[entry.Configuration.ProviderId] = new AIProviderSettings
            {
                Configuration = Clone(entry.Configuration), IsCodeDefined = baseline is not null, HasOverride = true,
                HasApiKey = !string.IsNullOrWhiteSpace(entry.ProtectedApiKey)
                    || entry.UseCodeApiKey && !string.IsNullOrWhiteSpace(baseline?.ApiKey),
                ValidationErrors = settings.ValidationErrors.GetValueOrDefault(entry.Configuration.ProviderId, [])
            };
        }

        return new AIConfigurationSnapshot
        {
            Revision = settings.Document.Revision,
            Providers = providers.Values.OrderBy(static entry => entry.Configuration.DisplayName ?? entry.Configuration.ProviderId, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private void EnsureOneDefault(List<AIPersistedProvider> entries)
    {
        var defaults = ProjectSnapshot(new ValidatedSettings(new AIConfigurationDocument { Providers = entries.ToArray() }, NO_VALIDATION_ERRORS)).Providers
            .Where(static entry => entry.Configuration.Enabled && entry.Configuration.IsDefault).ToArray();
        if (defaults.Length > 1)
        {
            throw new InvalidOperationException("Reset would restore more than one default provider. Clear the current default before resetting this provider.");
        }
    }

    private IDataProtector GetProtector(string providerId) => dataProtection.CreateProtector(
        "Monica.AI.ProviderCredentials.v1", providerId.ToUpperInvariant());

    private static bool SameId(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static AIProviderConfiguration Clone(AIProviderConfiguration configuration) => configuration with
    {
        Models = configuration.Models.Select(static model => model with { ReasoningLevels = model.ReasoningLevels.ToArray() }).ToArray()
    };

    private static AIProviderConfiguration Normalize(AIProviderConfiguration configuration) => Clone(configuration) with
    {
        ProviderId = configuration.ProviderId.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(configuration.DisplayName) ? null : configuration.DisplayName.Trim(),
        BaseUrl = string.IsNullOrWhiteSpace(configuration.BaseUrl) ? null : configuration.BaseUrl.Trim(),
        DefaultModel = string.IsNullOrWhiteSpace(configuration.DefaultModel) ? null : configuration.DefaultModel.Trim(),
        Models = configuration.Models.Select(static model => model with
        {
            ModelName = model.ModelName.Trim(),
            ReasoningLevels = model.ReasoningLevels.Select(static level => level with { Id = level.Id.Trim() }).ToArray()
        }).ToArray()
    };

    private static ValidatedSettings LoadSettings(IAIConfigurationStore store, AIConfigurationValidationMode mode) =>
        ValidateSettings(store.Read(), mode);

    private static ValidatedSettings ValidateSettings(AIConfigurationDocument document, AIConfigurationValidationMode mode)
    {
        if (document.Revision < 0 || document.Providers.Select(static entry => entry.Configuration.ProviderId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Providers.Count)
        {
            throw new InvalidDataException("The AI configuration document has an invalid revision or duplicate provider identifiers.");
        }

        var validationErrors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<AIPersistedProvider>();
        foreach (var entry in document.Providers)
        {
            var configuration = entry.Configuration;
            var findings = CollectConfigurationErrors(configuration);
            if (findings.Count > 0)
            {
                if (mode == AIConfigurationValidationMode.Throw)
                {
                    throw new ArgumentException(string.Join(" ", findings));
                }

                configuration = configuration with { Enabled = false };
                validationErrors[configuration.ProviderId] = findings;
            }

            entries.Add(entry with { Configuration = Clone(configuration) });
        }

        return new ValidatedSettings(
            new AIConfigurationDocument { Revision = document.Revision, Providers = [.. entries] }, validationErrors);
    }

    private static ValidatedDefaults LoadDefaults(IEnumerable<AIProviderDefinition> definitions, AIModelCatalog catalog, AIConfigurationValidationMode mode)
    {
        var providers = new Dictionary<string, CodeProvider>(StringComparer.OrdinalIgnoreCase);
        var validationErrors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var configuration = Normalize(definition.ToConfiguration(catalog));
            var findings = CollectConfigurationErrors(configuration);
            if (findings.Count > 0)
            {
                if (mode == AIConfigurationValidationMode.Throw)
                {
                    throw new ArgumentException(string.Join(" ", findings));
                }

                configuration = configuration with { Enabled = false };
                validationErrors[configuration.ProviderId] = findings;
            }

            if (!providers.TryAdd(configuration.ProviderId, new CodeProvider(configuration, definition.ApiKey)))
            {
                throw new InvalidOperationException($"Duplicate AI provider identifier '{configuration.ProviderId}'.");
            }
        }

        if (providers.Values.Count(static provider => provider.Configuration.Enabled && provider.Configuration.IsDefault) > 1)
        {
            throw new InvalidOperationException("Only one enabled AI provider may be configured as the default.");
        }

        return new ValidatedDefaults(providers, validationErrors);
    }

    private static void ValidateConfiguration(AIProviderConfiguration configuration)
    {
        var findings = CollectConfigurationErrors(configuration);
        if (findings.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", findings));
        }
    }

    private static IReadOnlyList<string> CollectConfigurationErrors(AIProviderConfiguration configuration)
    {
        var findings = new List<string>();
        if (string.IsNullOrWhiteSpace(configuration.ProviderId))
        {
            findings.Add("The provider identifier is required.");
        }
        else if (configuration.ProviderId.Length > 128 || configuration.ProviderId.Any(char.IsControl))
        {
            findings.Add("Provider identifiers must contain at most 128 characters and no control characters.");
        }

        if (configuration.ProviderType is not (EAIProviderType.OpenAI or EAIProviderType.Anthropic))
        {
            findings.Add("Runtime configuration supports OpenAI-compatible and Anthropic providers.");
            return findings;
        }

        if (configuration.TimeoutSeconds is < 1 or > 3600)
        {
            findings.Add("Request timeout must be between 1 and 3600 seconds.");
        }

        if (!Enum.IsDefined(configuration.OpenAIApiMode) || !Enum.IsDefined(configuration.OpenAIProtocolProfile)
            || !Enum.IsDefined(configuration.ResponsesHistoryMode)
            || configuration.PromptCacheRetention is { } retention && !Enum.IsDefined(retention))
        {
            findings.Add("The provider has an unsupported API mode, protocol profile, history mode, or cache-retention setting.");
        }

        if (configuration.BaseUrl is { } baseUrl
            && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
        {
            findings.Add("The provider endpoint must be an absolute HTTP(S) URL without embedded credentials, query, or fragment.");
        }

        if (configuration.Models.Select(static model => model.ModelName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Models.Count)
        {
            findings.Add("Model identifiers must be unique within a provider.");
        }

        foreach (var model in configuration.Models)
        {
            CollectModelError(configuration, model, findings);
        }

        if (configuration.DefaultModel is { } defaultModel
            && !configuration.Models.Any(model => model.Kind == AIModelKind.Chat && SameId(model.ModelName, defaultModel)))
        {
            findings.Add("The default model must be a configured chat model belonging to this provider.");
        }

        return findings;
    }

    private static void CollectModelError(AIProviderConfiguration configuration, AIModelConfiguration model, List<string> findings)
    {
        if (string.IsNullOrWhiteSpace(model.ModelName))
        {
            findings.Add("Every model requires a name.");
            return;
        }

        if (!Enum.IsDefined(model.Kind))
        {
            findings.Add($"Model '{model.ModelName}' has an unsupported model kind.");
            return;
        }

        if (model.ContextWindow is <= 0 || model.MaxOutputTokens is <= 0 || model.EmbeddingDimensions is <= 0
            || model.ContextWindow is { } context && model.MaxOutputTokens is { } output && output >= context)
        {
            findings.Add($"Model '{model.ModelName}' needs positive capacities and an output budget smaller than its context window.");
        }

        if (model.ReasoningLevels.Select(static level => level.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != model.ReasoningLevels.Count
            || model.ReasoningLevels.Any(static level => string.IsNullOrWhiteSpace(level.Id) || level.BudgetTokens is <= 0))
        {
            findings.Add($"Model '{model.ModelName}' has invalid or duplicate reasoning levels.");
        }

        if (model.SupportsReasoning == false && model.ReasoningLevels.Count > 0)
        {
            findings.Add($"Model '{model.ModelName}' cannot declare reasoning levels while reasoning support is disabled.");
        }

        foreach (var level in model.ReasoningLevels.Where(static level => level.BudgetTokens is not null))
        {
            if (configuration.ProviderType == EAIProviderType.OpenAI)
            {
                findings.Add("OpenAI-compatible model configuration supports reasoning effort but has no standard thinking-token budget field.");
                break;
            }

            if (level.BudgetTokens < 1024 || model.MaxOutputTokens is { } maximum && level.BudgetTokens >= maximum
                || string.Equals(level.ProviderValue, "none", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add($"Model '{model.ModelName}' requires a thinking budget of at least 1024, below its maximum output budget, with reasoning enabled.");
                break;
            }
        }

        if (model.DefaultReasoningLevel is { } selected
            && !model.ReasoningLevels.Any(level => SameId(level.Id, selected)))
        {
            findings.Add($"Model '{model.ModelName}' has a default reasoning level that is not configured.");
        }
    }

    private sealed record CodeProvider(AIProviderConfiguration Configuration, string? ApiKey);
    private sealed record ValidatedDefaults(
        IReadOnlyDictionary<string, CodeProvider> Providers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ValidationErrors);
    private sealed record ValidatedSettings(
        AIConfigurationDocument Document,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ValidationErrors);
}

internal sealed record EffectiveAIConfiguration(long Revision, IReadOnlyList<ResolvedAIProvider> Providers);
internal sealed record ResolvedAIProvider(
    AIProviderConfiguration Configuration,
    string? ApiKey,
    string? CredentialError,
    IReadOnlyList<string> ValidationErrors);
