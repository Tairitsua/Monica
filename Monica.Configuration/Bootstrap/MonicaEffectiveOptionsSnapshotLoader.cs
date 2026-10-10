using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Annotations;
using Monica.Configuration.Binding;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Bootstrap;

internal sealed class MonicaEffectiveOptionsSnapshotLoader : IDisposable, IAsyncDisposable
{
    private readonly IConfigurationEffectiveValueReader _reader;
    private readonly IConfiguration _hostConfiguration;
    private readonly string _contentRootPath;
    private readonly IReadOnlyList<ManagedJsonConfigurationSourceRegistration> _managedJsonSources;
    private readonly MonicaEffectiveOptionsSnapshotOptions _options;
    private readonly ConfigurationDefinitionScanner _definitionScanner;
    private readonly ConfigurationEffectiveValueSeedFactory _seedFactory;
    private readonly ConfigurationLocalDefinitionRegistry _localRegistry = new();
    private readonly ConfigurationObjectMaterializer _materializer;
    private readonly ILogger _logger;
    private bool _disposed;

    public MonicaEffectiveOptionsSnapshotLoader(
        MonicaBootstrapConfiguration bootstrapConfiguration,
        ConfigurationSectionPathConvention sectionPathConvention,
        MonicaEffectiveOptionsSnapshotOptions options,
        IConfigurationEffectiveValueReader reader,
        IReadOnlyList<ManagedJsonConfigurationSourceRegistration> managedJsonSources,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(bootstrapConfiguration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(managedJsonSources);
        ArgumentNullException.ThrowIfNull(logger);

        _reader = reader;
        _hostConfiguration = bootstrapConfiguration.HostConfiguration;
        _contentRootPath = bootstrapConfiguration.ContentRootPath;
        _managedJsonSources = managedJsonSources.ToArray();
        _options = options;
        _logger = logger;
        _definitionScanner = new ConfigurationDefinitionScanner(
            new ConfigurationSchemaHasher(),
            sectionPathConvention);

        var runtimeContext = new ConfigurationRuntimeContext();
        runtimeContext.Capture(bootstrapConfiguration);
        _materializer = new ConfigurationObjectMaterializer(_localRegistry);
        _seedFactory = new ConfigurationEffectiveValueSeedFactory(runtimeContext, _localRegistry, _materializer);
    }

    public MonicaEffectiveOptionsSnapshot Load(IReadOnlyCollection<Type> optionsTypes)
    {
        return LoadAsync(optionsTypes, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<MonicaEffectiveOptionsSnapshot> LoadAsync(
        IReadOnlyCollection<Type> optionsTypes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(optionsTypes);

        var requestedTypes = NormalizeRequestedTypes(optionsTypes);
        var loadedOptions = await LoadOptionsAsync(requestedTypes, cancellationToken);
        return new MonicaEffectiveOptionsSnapshot(loadedOptions);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_reader is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (_reader is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_reader is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_reader is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static IReadOnlyList<Type> NormalizeRequestedTypes(IEnumerable<Type> optionsTypes)
    {
        var result = new List<Type>();
        var seen = new HashSet<Type>();
        foreach (var optionsType in optionsTypes)
        {
            ArgumentNullException.ThrowIfNull(optionsType);
            if (!seen.Add(optionsType))
            {
                continue;
            }

            result.Add(optionsType);
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<Type, object>> LoadOptionsAsync(
        IReadOnlyList<Type> optionsTypes,
        CancellationToken cancellationToken)
    {
        var definitions = optionsTypes
            .Select(ScanOptionsType)
            .ToArray();
        _localRegistry.RegisterRange(optionsTypes.Zip(definitions,
            (type, definition) => new ConfigurationDefinitionRegistration(type, definition)));

        var definitionKeys = definitions
            .Select(static definition => definition.DefinitionKey)
            .ToArray();

        if (_options.Debugging)
        {
            _logger.LogInformation(
                "Reading {OptionsCount} Monica effective options from store '{StoreKey}'.",
                definitionKeys.Length,
                _reader.Descriptor.StoreKey);
        }

        var documents = await _reader.GetManyAsync(definitionKeys, cancellationToken);
        if (documents.Count != definitions.Length)
        {
            throw new InvalidOperationException(
                $"The Monica effective-value reader returned {documents.Count} documents for {definitions.Length} requested definitions.");
        }

        var monicaValues = ProjectDocuments(definitions, documents);
        var effectiveConfiguration = BuildConfiguration(monicaValues);
        try
        {
            var loadedOptions = new Dictionary<Type, object>();
            for (var i = 0; i < definitions.Length; i++)
            {
                loadedOptions[optionsTypes[i]] = BindOptions(
                    optionsTypes[i],
                    definitions[i],
                    effectiveConfiguration);
            }

            return loadedOptions;
        }
        finally
        {
            (effectiveConfiguration as IDisposable)?.Dispose();
        }
    }

    private ConfigurationDefinition ScanOptionsType(Type optionsType)
    {
        if (optionsType.GetCustomAttribute<ConfigurationAttribute>(inherit: false) is null)
        {
            throw new InvalidOperationException(
                $"Options type '{optionsType.FullName ?? optionsType.Name}' cannot be read from Monica effective values because it is not marked with {nameof(ConfigurationAttribute)}.");
        }

        try
        {
            return _definitionScanner.Scan(optionsType);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to scan Monica effective options type '{optionsType.FullName ?? optionsType.Name}'.",
                ex);
        }
    }

    private ConfigurationValueProjection ProjectDocuments(
        IReadOnlyList<ConfigurationDefinition> definitions,
        IReadOnlyList<ConfigurationEffectiveValueDocument?> documents)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var shapes = new Dictionary<string, ConfigurationValueShape>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            var document = documents[i];
            if (document is not null
                && !string.Equals(definition.DefinitionKey, document.DefinitionKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The Monica effective-value reader returned document '{document.DefinitionKey}' for requested definition '{definition.DefinitionKey}'.");
            }

            try
            {
                var json = document?.Json ?? _seedFactory.CreateSeedJson(definition);
                var projection = _materializer.Project(definition, json);
                foreach (var (key, value) in projection.Values)
                {
                    values[key] = value;
                }
                foreach (var (key, shape) in projection.Shapes) shapes[key] = shape;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to project Monica effective JSON document for definition '{definition.DefinitionKey}'.",
                    ex);
            }
        }

        return new ConfigurationValueProjection(values, shapes);
    }

    private IConfigurationRoot BuildConfiguration(ConfigurationValueProjection monicaValues)
    {
        var builder = new ConfigurationBuilder();
        if (!string.IsNullOrWhiteSpace(_contentRootPath))
        {
            builder.SetBasePath(_contentRootPath);
        }

        builder.AddConfiguration(_hostConfiguration);
        builder.Add(new ConfigurationValueProjectionSource(monicaValues));

        foreach (var source in _managedJsonSources)
        {
            builder.AddShapeAwareJsonFile(source.Path, source.Optional, reloadOnChange: false);
        }

        return builder.Build();
    }

    private object BindOptions(
        Type optionsType,
        ConfigurationDefinition definition,
        IConfiguration configuration)
    {
        try
        {
            return _materializer.Materialize(definition, configuration);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to bind Monica effective options type '{optionsType.FullName ?? optionsType.Name}' from section '{definition.SectionPath}'.",
                ex);
        }
    }

}
