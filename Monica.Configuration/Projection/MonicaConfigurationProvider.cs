using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Binding;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;
using Monica.Modules;

namespace Monica.Configuration.Projection;

/// <summary>
/// Single Microsoft.Extensions.Configuration provider that exposes Monica effective value documents.
/// </summary>
internal sealed class MonicaConfigurationProvider(MonicaConfigurationProviderAccessor accessor)
    : ConfigurationProvider, IConfigurationValueShapeProvider
{
    private readonly Lock _projectionLock = new();
    private readonly Dictionary<string, IReadOnlyCollection<string>> _projectedKeysByDefinitionKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long?> _loadedVersionsByDefinitionKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyCollection<string>> _projectedShapesByDefinitionKey = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, ConfigurationValueShape> _shapes = new Dictionary<string, ConfigurationValueShape>(StringComparer.OrdinalIgnoreCase);
    private long _reloadCount;
    private long _failedReloadCount;
    private DateTimeOffset? _lastReloadedAt;
    private DateTimeOffset? _lastFailedAt;
    private TimeSpan? _lastReloadDuration;
    private string? _lastFailureMessage;
    private long _successfulProjectionRevision;

    /// <inheritdoc />
    public override void Load()
    {
        ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reloads effective value documents and emits a new flat configuration projection.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (accessor.ServiceProvider is null)
        {
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        var started = TimeProvider.System.GetTimestamp();
        var definitionRegistry = accessor.ServiceProvider.GetRequiredService<IConfigurationDefinitionRegistry>();
        var effectiveValueStore = accessor.ServiceProvider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var seedFactory = accessor.ServiceProvider.GetRequiredService<ConfigurationEffectiveValueSeedFactory>();
        var materializer = accessor.ServiceProvider.GetRequiredService<ConfigurationObjectMaterializer>();
        var metricsRecorder = accessor.ServiceProvider.GetRequiredService<ConfigurationMetricsRecorder>();
        var stateTracker = accessor.ServiceProvider.GetRequiredService<IConfigurationStoreStateTracker>();

        try
        {
            var projected = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var shapes = new Dictionary<string, ConfigurationValueShape>(StringComparer.OrdinalIgnoreCase);
            var definitions = definitionRegistry.GetAll();
            var seeds = definitions
                .Select(definition => new ConfigurationEffectiveValueSeed(
                    definition,
                    () => CreateValidatedSeed(definition, seedFactory)))
                .ToArray();
            var documents = await effectiveValueStore.EnsureCreatedAsync(seeds, cancellationToken);

            var projectedKeysByDefinitionKey = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            var loadedVersionsByDefinitionKey = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            var projectedShapesByDefinitionKey = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (definition, document) in definitions.Zip(documents))
            {
                var definitionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var projection = materializer.Project(definition, document.Json);
                foreach (var (key, value) in projection.Values)
                {
                    projected[key] = value;
                    definitionKeys.Add(key);
                }

                foreach (var (key, shape) in projection.Shapes) shapes[key] = shape;
                projectedShapesByDefinitionKey[definition.DefinitionKey] = projection.Shapes.Keys.ToArray();

                projectedKeysByDefinitionKey[definition.DefinitionKey] = definitionKeys;
                loadedVersionsByDefinitionKey[definition.DefinitionKey] = document.Version;
            }

            lock (_projectionLock)
            {
                Data = projected;
                _shapes = shapes;
                _projectedShapesByDefinitionKey.Clear();
                foreach (var (key, keys) in projectedShapesByDefinitionKey) _projectedShapesByDefinitionKey[key] = keys;
                _projectedKeysByDefinitionKey.Clear();
                foreach (var (definitionKey, keys) in projectedKeysByDefinitionKey)
                {
                    _projectedKeysByDefinitionKey[definitionKey] = keys;
                }

                _loadedVersionsByDefinitionKey.Clear();
                foreach (var (definitionKey, version) in loadedVersionsByDefinitionKey)
                {
                    _loadedVersionsByDefinitionKey[definitionKey] = version;
                }

                _successfulProjectionRevision++;
            }

            stateTracker.RecordSuccess(effectiveValueStore.Descriptor.StoreKey);
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            RecordReloadSuccess(elapsed);
            metricsRecorder.RecordReloadLatency(elapsed);
            OnReload();
        }
        catch (Exception ex)
        {
            RecordReloadFailure(ex);
            stateTracker.RecordFailure(effectiveValueStore.Descriptor.StoreKey, ex);
            throw;
        }
    }

    /// <summary>
    /// Reloads one effective-value definition into the flat configuration projection.
    /// </summary>
    /// <param name="definitionKey">The definition key to reload.</param>
    /// <param name="minimumVersion">Optional minimum version already observed in a reload signal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ReloadDefinitionAsync(string definitionKey, long? minimumVersion, CancellationToken cancellationToken)
    {
        if (accessor.ServiceProvider is null)
        {
            return;
        }

        if (minimumVersion is not null && GetLoadedVersion(definitionKey) >= minimumVersion)
        {
            return;
        }

        var started = TimeProvider.System.GetTimestamp();
        var definitionRegistry = accessor.ServiceProvider.GetRequiredService<IConfigurationDefinitionRegistry>();
        if (!definitionRegistry.TryGet(definitionKey, out var definition))
        {
            return;
        }

        var effectiveValueStore = accessor.ServiceProvider.GetRequiredService<IConfigurationEffectiveValueStore>();
        var seedFactory = accessor.ServiceProvider.GetRequiredService<ConfigurationEffectiveValueSeedFactory>();
        var materializer = accessor.ServiceProvider.GetRequiredService<ConfigurationObjectMaterializer>();
        var metricsRecorder = accessor.ServiceProvider.GetRequiredService<ConfigurationMetricsRecorder>();
        var stateTracker = accessor.ServiceProvider.GetRequiredService<IConfigurationStoreStateTracker>();

        try
        {
            var resolvedDefinition = definition!;
            var document = (await effectiveValueStore.EnsureCreatedAsync(
                [new ConfigurationEffectiveValueSeed(
                    resolvedDefinition,
                    () => CreateValidatedSeed(resolvedDefinition, seedFactory))],
                cancellationToken))[0];
            var loadedVersion = GetLoadedVersion(definitionKey);
            if (loadedVersion is not null && document.Version <= loadedVersion)
            {
                return;
            }

            var projected = materializer.Project(resolvedDefinition, document.Json);
            lock (_projectionLock)
            {
                var nextData = new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
                var nextShapes = new Dictionary<string, ConfigurationValueShape>(_shapes, StringComparer.OrdinalIgnoreCase);
                if (_projectedShapesByDefinitionKey.TryGetValue(resolvedDefinition.DefinitionKey, out var oldShapes))
                    foreach (var key in oldShapes) nextShapes.Remove(key);
                if (_projectedKeysByDefinitionKey.TryGetValue(resolvedDefinition.DefinitionKey, out var oldKeys))
                {
                    foreach (var key in oldKeys)
                    {
                        nextData.Remove(key);
                    }
                }

                foreach (var (key, value) in projected.Values)
                {
                    nextData[key] = value;
                }

                Data = nextData;
                foreach (var (key, shape) in projected.Shapes) nextShapes[key] = shape;
                _shapes = nextShapes;
                _projectedShapesByDefinitionKey[resolvedDefinition.DefinitionKey] = projected.Shapes.Keys.ToArray();
                _projectedKeysByDefinitionKey[resolvedDefinition.DefinitionKey] = projected.Values.Keys.ToArray();
                _loadedVersionsByDefinitionKey[resolvedDefinition.DefinitionKey] = document.Version;
                _successfulProjectionRevision++;
            }

            stateTracker.RecordSuccess(effectiveValueStore.Descriptor.StoreKey);
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            RecordReloadSuccess(elapsed);
            metricsRecorder.RecordReloadLatency(elapsed);
            OnReload();
        }
        catch (Exception ex)
        {
            RecordReloadFailure(ex);
            stateTracker.RecordFailure(effectiveValueStore.Descriptor.StoreKey, ex);
            throw;
        }
    }

    public bool TryGetShape(string path, out ConfigurationValueShape shape)
    {
        lock (_projectionLock) return _shapes.TryGetValue(path, out shape);
    }

    /// <inheritdoc />
    public override void Set(string key, string? value)
    {
        lock (_projectionLock)
        {
            var next = ConfigurationValueProjectionFactory.Set(new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase), _shapes, key, value);
            _shapes = next.Shapes;
            Data = new Dictionary<string, string?>(next.Values, StringComparer.OrdinalIgnoreCase);
            foreach (var definitionKey in _projectedShapesByDefinitionKey.Keys.ToArray())
            {
                var section = _projectedShapesByDefinitionKey[definitionKey].MinBy(path => path.Length);
                if (section is null || !(string.Equals(key, section, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith($"{section}:", StringComparison.OrdinalIgnoreCase))) continue;
                _projectedKeysByDefinitionKey[definitionKey] = next.Values.Keys.Where(path =>
                    string.Equals(path, section, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith($"{section}:", StringComparison.OrdinalIgnoreCase)).ToArray();
                _projectedShapesByDefinitionKey[definitionKey] = next.Shapes.Keys.Where(path =>
                    string.Equals(path, section, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith($"{section}:", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            Interlocked.Increment(ref _successfulProjectionRevision);
        }
    }

    private string CreateValidatedSeed(ConfigurationDefinition definition, ConfigurationEffectiveValueSeedFactory seedFactory)
    {
        var provider = accessor.ServiceProvider!;
        var json = seedFactory.CreateSeedJson(definition);
        var result = provider.GetRequiredService<ConfigurationValidationCoordinator>().ValidateCompleteValue(
            definition, json, ConfigurationValidationProfile.Seed);
        if (result.Coverage == ConfigurationValidationCoverage.Failed)
            throw new ConfigurationValidationExecutionException(definition.DefinitionKey, LogicalPath.Root, "seed-materialization");
        if (!result.IsValid)
        {
            var report = ConfigurationValidationReportFactory.Create(definition, result);
            if (provider.GetRequiredService<ConfigurationValidationPolicy>().Behavior == ConfigurationRuntimeValidationBehavior.FailFast)
                throw new ConfigurationRuntimeValidationException(report);
            provider.GetRequiredService<ILogger<MonicaConfigurationProvider>>().LogWarning("{ConfigurationSeedDiagnostic}",
                ConfigurationRuntimeValidationMessageFormatter.FormatDiagnosticReport(report));
        }
        return json;
    }

    /// <summary>
    /// Gets the loaded effective-value document version for one definition.
    /// </summary>
    /// <param name="definitionKey">Definition key.</param>
    /// <returns>The loaded version, or null when the definition has not been loaded.</returns>
    public long? GetLoadedVersion(string definitionKey)
    {
        lock (_projectionLock)
        {
            return _loadedVersionsByDefinitionKey.GetValueOrDefault(definitionKey);
        }
    }

    /// <summary>
    /// Gets the monotonic revision of the last successfully committed runtime projection.
    /// </summary>
    internal long SuccessfulProjectionRevision
    {
        get
        {
            lock (_projectionLock)
            {
                return _successfulProjectionRevision;
            }
        }
    }

    /// <summary>
    /// Gets the current reload state for this provider.
    /// </summary>
    /// <returns>The reload state.</returns>
    public ConfigurationRuntimeReloadState GetReloadState()
    {
        lock (_projectionLock)
        {
            return new ConfigurationRuntimeReloadState
            {
                IsProviderActive = accessor.ServiceProvider is not null,
                ReloadCount = _reloadCount,
                LastReloadedAt = _lastReloadedAt,
                LastReloadDuration = _lastReloadDuration,
                FailedReloadCount = _failedReloadCount,
                LastFailedAt = _lastFailedAt,
                LastFailureMessage = _lastFailureMessage
            };
        }
    }

    private void RecordReloadSuccess(TimeSpan elapsed)
    {
        lock (_projectionLock)
        {
            _reloadCount++;
            _lastReloadedAt = DateTimeOffset.UtcNow;
            _lastReloadDuration = elapsed;
            _lastFailureMessage = null;
        }
    }

    private void RecordReloadFailure(Exception exception)
    {
        lock (_projectionLock)
        {
            _failedReloadCount++;
            _lastFailedAt = DateTimeOffset.UtcNow;
            _lastFailureMessage = exception.Message;
        }
    }
}
