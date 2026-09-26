using System.Text.Json;
using Monica.AI.Abstractions;
using Monica.AI.Configuration.Services;
using Monica.AI.Models;
using Monica.AI.Providers;
using Monica.AI.Providers.Anthropic;
using Monica.AI.Providers.OpenAI;

namespace Monica.AI.Services;

/// <summary>
/// Publishes immutable provider generations. A replaced generation is disposed after its final run or embedding lease.
/// </summary>
internal sealed class AIProviderRegistry(
    IEnumerable<IAIProvider> providers,
    AIConfigurationService configuration,
    AIModelCatalog catalog) : IAIProviderFactory, IDisposable
{
    private readonly object _gate = new();
    private readonly AIConfigurationService _configuration = configuration;
    private readonly AIModelCatalog _catalog = catalog;
    private readonly IReadOnlyDictionary<string, IAIProvider> _customProviders = IndexCustomProviders(providers);
    private Dictionary<string, ProviderGeneration> _providers = new(StringComparer.OrdinalIgnoreCase);
    private long _revision = -1;
    private string? _defaultProviderId;
    private bool _disposed;

    private static IReadOnlyDictionary<string, IAIProvider> IndexCustomProviders(IEnumerable<IAIProvider> providers)
    {
        var customProviders = new Dictionary<string, IAIProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (!customProviders.TryAdd(provider.ProviderId, provider))
            {
                throw new InvalidOperationException($"Duplicate AI provider identifier '{provider.ProviderId}'.");
            }
        }

        return customProviders;
    }

    public IAIProviderLease? AcquireProvider(string? providerId = null)
    {
        lock (_gate)
        {
            Refresh();
            var id = string.IsNullOrWhiteSpace(providerId) ? _defaultProviderId : providerId;
            return id is not null && _providers.TryGetValue(id, out var generation)
                ? generation.Acquire(_revision)
                : null;
        }
    }

    public AIProviderInfo? GetProviderInfo(string providerId)
    {
        lock (_gate)
        {
            Refresh();
            return _providers.GetValueOrDefault(providerId)?.Provider.Info;
        }
    }

    public IReadOnlyList<AIProviderInfo> GetAllProviderInfos()
    {
        lock (_gate)
        {
            Refresh();
            return _providers.Values.Select(static generation => generation.Provider.Info).ToArray();
        }
    }

    public AIProviderInfo? GetDefaultProviderInfo()
    {
        lock (_gate)
        {
            Refresh();
            return _defaultProviderId is { } id ? _providers.GetValueOrDefault(id)?.Provider.Info : null;
        }
    }

    public bool HasProvider(string providerId) => GetProviderInfo(providerId) is not null;

    private void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_revision == _configuration.CurrentRevision)
        {
            return;
        }

        var configuration = _configuration.Resolve();
        var next = new Dictionary<string, ProviderGeneration>(StringComparer.OrdinalIgnoreCase);
        var created = new List<ProviderGeneration>();
        try
        {
            foreach (var definition in configuration.Providers)
            {
                var id = definition.Configuration.ProviderId;
                if (_customProviders.ContainsKey(id))
                {
                    throw new InvalidOperationException($"Runtime configuration and a custom provider share identifier '{id}'.");
                }

                var signature = JsonSerializer.Serialize(definition.Configuration);
                if (_providers.TryGetValue(id, out var current) && current.Matches(signature, definition.ApiKey, definition.CredentialError, definition.ValidationErrors))
                {
                    next.Add(id, current);
                    continue;
                }

                var generation = new ProviderGeneration(CreateProvider(definition), signature, definition.ApiKey, definition.CredentialError, definition.ValidationErrors, ownsProvider: true);
                created.Add(generation);
                next.Add(id, generation);
            }

            foreach (var provider in _customProviders.Values)
            {
                if (!_providers.TryGetValue(provider.ProviderId, out var generation))
                {
                    generation = new ProviderGeneration(provider, null, null, null, [], ownsProvider: false);
                    created.Add(generation);
                }

                next.Add(provider.ProviderId, generation);
            }

            var available = next.Values.Where(static generation => generation.Provider.Info.IsValid).ToArray();
            var defaults = available.Where(static generation => generation.Provider.Info.IsDefault).ToArray();
            if (defaults.Length > 1)
            {
                throw new InvalidOperationException("Only one valid AI provider may be configured as the default.");
            }

            _defaultProviderId = (defaults.FirstOrDefault() ?? available.FirstOrDefault())?.Provider.ProviderId;
        }
        catch
        {
            foreach (var generation in created)
            {
                generation.Release();
            }

            throw;
        }

        foreach (var previous in _providers.Values)
        {
            if (!next.TryGetValue(previous.Provider.ProviderId, out var replacement) || !ReferenceEquals(previous, replacement))
            {
                previous.Release();
            }
        }

        _providers = next;
        _revision = configuration.Revision;
    }

    private IAIProvider CreateProvider(ResolvedAIProvider resolved)
    {
        var configuration = resolved.Configuration;
        var options = AIConfiguredProviderFactory.CreateOptions(configuration, resolved.ApiKey);

        var errors = new List<string>(resolved.ValidationErrors);
        if (!configuration.Enabled) errors.Add("Provider is disabled in host settings.");
        if (resolved.CredentialError is { } credentialError) errors.Add(credentialError);
        else if (string.IsNullOrWhiteSpace(resolved.ApiKey)) errors.Add("API key is empty. Configure a non-empty API key to enable this provider.");

        if (errors.Count == 0)
        {
            try
            {
                return AIConfiguredProviderFactory.Create(options, _catalog);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // SDK constructor messages may include endpoint or credential data; keep public diagnostics bounded.
                errors.Add("Provider initialization failed. Check endpoint and protocol settings.");
            }
        }

        return DisabledAIProvider.FromOptions(options, _catalog, configuration.ProviderType.ToString(),
            "Configured AI provider", configuration.ProviderType.ToString().ToLowerInvariant(), errors);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var generation in _providers.Values) generation.Release();
            _providers.Clear();
        }
    }

    private sealed class ProviderGeneration(
        IAIProvider provider,
        string? signature,
        string? apiKey,
        string? credentialError,
        IReadOnlyList<string> validationErrors,
        bool ownsProvider)
    {
        private int _references = 1;
        public IAIProvider Provider { get; } = provider;

        public bool Matches(string candidateSignature, string? candidateKey, string? candidateError, IReadOnlyList<string> candidateValidationErrors) =>
            string.Equals(signature, candidateSignature, StringComparison.Ordinal)
            && string.Equals(apiKey, candidateKey, StringComparison.Ordinal)
            && string.Equals(credentialError, candidateError, StringComparison.Ordinal)
            && validationErrors.SequenceEqual(candidateValidationErrors);

        public IAIProviderLease Acquire(long revision)
        {
            Interlocked.Increment(ref _references);
            return new ProviderLease(this, revision);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0 && ownsProvider) Provider.Dispose();
        }

        public string RedactDiagnostic(string message) => string.IsNullOrEmpty(apiKey)
            ? message
            : message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);

        private sealed class ProviderLease(ProviderGeneration generation, long revision) : IAIProviderLease
        {
            private ProviderGeneration? _generation = generation;
            public IAIProvider Provider => _generation?.Provider ?? throw new ObjectDisposedException(nameof(ProviderLease));
            public long ConfigurationRevision { get; } = revision;
            public string RedactDiagnostic(string message) =>
                (_generation ?? throw new ObjectDisposedException(nameof(ProviderLease))).RedactDiagnostic(message);
            public void Dispose() => Interlocked.Exchange(ref _generation, null)?.Release();
        }
    }
}
