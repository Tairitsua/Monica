using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Configuration.Annotations;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Core;
using Monica.Core.Configuration;

namespace Monica.Configuration.Services.Support;

/// <summary>Owns validated startup objects for one host; neither binding nor diagnostics require a managed store.</summary>
internal sealed class ConfigurationStartupOptionsReader
{
    private readonly MonicaCompositionState _composition;
    private readonly ConfigurationLocalDefinitionRegistry _localRegistry = new();
    private readonly ConfigurationDefinitionScanner _scanner = new(new ConfigurationSchemaHasher());
    private readonly ConfigurationObjectMaterializer _materializer;
    private readonly ConfigurationValidationCoordinator _validation;
    private readonly Dictionary<Type, ConfigurationStartupEntry> _entries = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<ConfigurationDefinition> _definitions = [];
    private bool _finalized;

    private ConfigurationStartupOptionsReader(MonicaCompositionState composition)
    {
        _composition = composition;
        _materializer = new ConfigurationObjectMaterializer(_localRegistry);
        _validation = new ConfigurationValidationCoordinator(new ConfigurationValueValidationEngine(), _localRegistry,
            _materializer, new ConfigurationObjectValidationWalker());
        composition.OnServicesFinalized(FinalizeServices, ValidateFinalServices);
    }

    internal static ConfigurationStartupOptionsReader For(MonicaCompositionState composition)
        => composition.GetOrAdd(() => new ConfigurationStartupOptionsReader(composition));

    internal IConfigurationRoot Snapshot => _composition.StartupConfiguration;

    internal TOptions Get<TOptions>() where TOptions : class, new() => (TOptions)Get(typeof(TOptions)).Instance;

    private ConfigurationStartupEntry Get(Type optionsType)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(optionsType, out var existing)) return existing;
            if (_finalized)
                throw new InvalidOperationException("Startup options must be read or discovered before service registration completes.");
            if (optionsType.GetCustomAttribute<ConfigurationAttribute>(inherit: false)?.Kind != ConfigurationKind.Startup)
                throw new InvalidOperationException($"Options type '{optionsType.FullName}' must declare Startup configuration ownership.");
            var definition = _scanner.Scan(optionsType);
            if (_entries.Values.Any(entry => string.Equals(entry.Definition.DefinitionKey, definition.DefinitionKey,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Startup configuration key '{definition.DefinitionKey}' has more than one CLR owner.");
            _localRegistry.RegisterRange([new ConfigurationDefinitionRegistration(optionsType, definition)]);
            object instance;
            ConfigurationCompleteValidationResult validation;
            try { (instance, validation) = _validation.MaterializeAndValidate(definition, Snapshot); }
            catch (ConfigurationValidationExecutionException fault)
            {
                throw new ConfigurationRuntimeValidationException(CreateFailureReport(definition, fault,
                    "The Startup input could not be bound or validated."), startup: true);
            }
            var report = ConfigurationValidationReportFactory.Create(definition, validation);
            if (!report.IsValid) throw new ConfigurationRuntimeValidationException(report, startup: true);
            var entry = new ConfigurationStartupEntry(optionsType, definition, instance,
                _materializer.Snapshot(definition, instance), report);
            _entries.Add(optionsType, entry);
            return entry;
        }
    }

    internal void RegisterDefinitions(IReadOnlyList<ConfigurationDefinitionRegistration> registrations)
    {
        lock (_gate)
        {
            _definitions = registrations.Select(registration => registration.Definition).ToArray();
            foreach (var registration in registrations.Where(registration => registration.Definition.Kind == ConfigurationKind.Startup))
            {
                var entry = Get(registration.OptionsType);
                EnsureSameDefinition(entry.Definition, registration.Definition);
            }
        }
    }

    internal ConfigurationStartupEntry GetRequired(ConfigurationDefinition definition)
    {
        lock (_gate)
        {
            var entry = _entries.Values.FirstOrDefault(candidate => string.Equals(
                candidate.Definition.DefinitionKey, definition.DefinitionKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Startup definition '{definition.DefinitionKey}' was not validated during composition.");
            EnsureSameDefinition(entry.Definition, definition);
            return entry;
        }
    }

    internal ConfigurationStartupCandidateSnapshot ReadCandidate(ConfigurationDefinition definition)
    {
        var actual = GetRequired(definition);
        ConfigurationSnapshot? source = null;
        try
        {
            source = ConfigurationSnapshot.Capture((IConfigurationRoot)_composition.LiveConfiguration);
            var (instance, validation) = _validation.MaterializeAndValidate(definition, source);
            var json = _materializer.Snapshot(definition, instance);
            var report = ConfigurationValidationReportFactory.Create(definition, validation);
            return new(json, report, !report.IsValid || !ConfigurationJsonSemanticComparer.Equals(actual.Json, json), source);
        }
        catch (Exception exception)
        {
            var report = CreateFailureReport(definition, exception as ConfigurationValidationExecutionException,
                "The deployment candidate could not be bound or validated. The running startup value is unchanged.");
            string json;
            try { json = source is null ? "null" : _materializer.ReadConfigurationJson(definition, source); }
            catch { json = "null"; }
            return new(json, report, true, source ?? ConfigurationSnapshot.Capture(new ConfigurationBuilder().Build()));
        }
    }

    private static ConfigurationValidationReport CreateFailureReport(ConfigurationDefinition definition,
        ConfigurationValidationExecutionException? fault, string message)
    {
        var path = fault?.LogicalPath ?? LogicalPath.Root;
        return ConfigurationValidationReportFactory.Create(definition, new ConfigurationCompleteValidationResult
        {
            Coverage = ConfigurationValidationCoverage.Failed,
            ValidationRevision = definition.ValidationContract.Revision,
            Issues = [new ConfigurationValueValidationIssue
            {
                LogicalPath = path,
                Node = ConfigurationSchemaNavigator.ResolveNode(definition.Root, path) ?? definition.Root,
                Kind = fault?.Kind ?? ConfigurationValidationIssueKind.Execution,
                Message = message
            }]
        });
    }

    internal ConfigurationEffectiveValue ProjectValue(ConfigurationDefinition definition, LogicalPath path,
        ConfigurationStartupCandidateSnapshot candidate, ConfigurationSourceChain actualChain,
        ConfigurationSourceChain candidateChain, bool includeSensitive)
    {
        var actual = GetRequired(definition);
        var node = ConfigurationSchemaNavigator.ResolveNode(definition.Root, path);
        var sensitive = ConfigurationSchemaNavigator.IsSensitivePath(definition.Root, path)
            || node is not null && ConfigurationEffectiveStateReader.ContainsSensitiveValue(node);
        var editor = new ConfigurationEffectiveValueDocumentEditor(new ConfigurationEffectiveValuePatchEngine(), new ConfigurationStoredValueCodec());
        var actualValue = editor.ReadValue(definition, actual.Json, path);
        ConfigurationStoredValue? candidateValue;
        try { candidateValue = editor.ReadValue(definition, candidate.Json, path); }
        catch { candidateValue = null; }
        var actualSource = node?.NodeKind == ConfigurationNodeKind.Scalar
            ? actualChain.Values.FirstOrDefault(value => value.IsEffective)?.Source : null;
        var candidateSource = node?.NodeKind == ConfigurationNodeKind.Scalar
            ? candidateChain.Values.FirstOrDefault(value => value.IsEffective)?.Source : null;
        return new ConfigurationEffectiveValue
        {
            DefinitionKey = definition.DefinitionKey,
            LogicalPath = path,
            ConfigurationPath = actualChain.ConfigurationPath,
            DisplayValue = sensitive && !includeSensitive ? null : ConfigurationEffectiveStateReader.ToDisplayValue(actualValue, node),
            IsSensitive = sensitive,
            EffectiveSource = actualSource,
            StartupCandidate = new ConfigurationStartupCandidate
            {
                DisplayValue = sensitive && !includeSensitive ? null : ConfigurationEffectiveStateReader.ToDisplayValue(candidateValue, node),
                EffectiveSource = candidateSource,
                RequiresRestart = !candidate.ValidationReport.IsValid
                    || !ConfigurationJsonSemanticComparer.Equals(actualValue?.Json ?? "null", candidateValue?.Json ?? "null"),
                ValidationReport = candidate.ValidationReport
            }
        };
    }

    private void FinalizeServices(IServiceCollection services)
    {
        lock (_gate)
        {
            var managed = _definitions.Where(definition => definition.Kind == ConfigurationKind.Managed).ToArray();
            foreach (var entry in _entries.Values)
            {
                if (managed.Any(definition => Overlaps(definition.SectionPath, entry.Definition.SectionPath)))
                    throw new InvalidOperationException($"Startup section '{entry.Definition.SectionPath}' overlaps a managed configuration section.");
            }

            _composition.ValidateEnvironmentFiles(snapshot => ValidateEnvironmentFile(snapshot, managed));
            foreach (var entry in _entries.Values)
            {
                typeof(ConfigurationStartupOptionsReader).GetMethod(nameof(RegisterFrozenOptions), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(entry.OptionsType).Invoke(null, [services, entry.Instance]);
            }
            _finalized = true;
        }
    }

    private void ValidateFinalServices(IReadOnlyList<ServiceDescriptor> services)
    {
        foreach (var entry in _entries.Values) RejectOptionsContributions(services, entry);
    }

    private static void EnsureSameDefinition(ConfigurationDefinition expected, ConfigurationDefinition discovered)
    {
        if (discovered.Kind != ConfigurationKind.Startup
            || !string.Equals(expected.SectionPath, discovered.SectionPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.SchemaHash, discovered.SchemaHash, StringComparison.Ordinal)
            || expected.ValidationContract != discovered.ValidationContract)
            throw new InvalidOperationException($"Early and discovered startup contracts disagree for '{expected.DefinitionKey}'.");
    }

    private static void RejectOptionsContributions(IReadOnlyList<ServiceDescriptor> services, ConfigurationStartupEntry entry)
    {
        var optionsType = entry.OptionsType;
        Type[] conflictingTypes = [typeof(IConfigureOptions<>), typeof(IPostConfigureOptions<>), typeof(IValidateOptions<>),
            typeof(IOptions<>), typeof(IOptionsSnapshot<>), typeof(IOptionsMonitor<>), typeof(IOptionsFactory<>),
            typeof(IOptionsMonitorCache<>), typeof(IOptionsChangeTokenSource<>)];
        var identities = conflictingTypes.Select(type => type.MakeGenericType(optionsType)).ToHashSet();
        if (services.Any(descriptor => identities.Contains(descriptor.ServiceType) && !IsFrozenOptionsService(descriptor, entry)
            || conflictingTypes.Contains(descriptor.ServiceType) && !IsDefaultOptionsService(descriptor)))
            throw new InvalidOperationException($"Startup options '{optionsType.FullName}' cannot use named options, Configure/PostConfigure, validators, change tokens, or custom options services. Use deployment inputs and pure configuration validation.");
        Type[] requiredAdapters = [typeof(IOptions<>), typeof(IOptionsSnapshot<>), typeof(IOptionsMonitor<>), typeof(IOptionsFactory<>)];
        foreach (var adapter in requiredAdapters)
        {
            var identity = adapter.MakeGenericType(optionsType);
            var descriptors = services.Where(descriptor => descriptor.ServiceType == identity).ToArray();
            if (descriptors.Length != 1 || !IsFrozenOptionsService(descriptors[0], entry))
                throw new InvalidOperationException($"Startup options '{optionsType.FullName}' require their frozen {adapter.Name} registration. Do not remove or replace Startup options adapters.");
        }
    }

    private static bool IsFrozenOptionsService(ServiceDescriptor descriptor, ConfigurationStartupEntry entry)
        => !descriptor.IsKeyedService && descriptor.ImplementationInstance is IFrozenStartupOptions frozen
            && frozen.OptionsType == entry.OptionsType && ReferenceEquals(frozen.Instance, entry.Instance);

    private static bool IsDefaultOptionsService(ServiceDescriptor descriptor)
    {
        if (descriptor.IsKeyedService) return false;
        var implementation = descriptor.ImplementationType;
        if (implementation?.Assembly != typeof(IOptions<>).Assembly) return false;
        var expectedName = descriptor.ServiceType == typeof(IOptions<>) ? "UnnamedOptionsManager`1"
            : descriptor.ServiceType == typeof(IOptionsSnapshot<>) ? "OptionsManager`1"
            : descriptor.ServiceType == typeof(IOptionsMonitor<>) ? "OptionsMonitor`1"
            : descriptor.ServiceType == typeof(IOptionsFactory<>) ? "OptionsFactory`1"
            : descriptor.ServiceType == typeof(IOptionsMonitorCache<>) ? "OptionsCache`1" : null;
        return expectedName is not null
            && implementation.FullName == $"Microsoft.Extensions.Options.{expectedName}";
    }

    private static void RegisterFrozenOptions<TOptions>(IServiceCollection services, object instance) where TOptions : class
    {
        var frozen = new FrozenStartupOptions<TOptions>((TOptions)instance);
        services.AddSingleton<IOptions<TOptions>>(frozen);
        services.AddSingleton<IOptionsSnapshot<TOptions>>(frozen);
        services.AddSingleton<IOptionsMonitor<TOptions>>(frozen);
        services.AddSingleton<IOptionsFactory<TOptions>>(frozen);
    }

    private static void ValidateEnvironmentFile(ConfigurationProviderSnapshot snapshot, IReadOnlyList<ConfigurationDefinition> managed)
    {
        foreach (var definition in managed)
        {
            var path = definition.SectionPath;
            if (snapshot.Shapes.Keys.Concat(snapshot.Values.Keys).Any(key => IsWithin(key, path))
                || ConfigurationShapeView.HasBarrier(snapshot, path))
                throw new ConfigurationSourcePathException(path);
        }
    }

    private static bool Overlaps(string left, string right) => IsWithin(left, right) || IsWithin(right, left);
    private static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase);

    private interface IFrozenStartupOptions
    {
        Type OptionsType { get; }
        object Instance { get; }
    }

    private sealed class FrozenStartupOptions<TOptions>(TOptions value) : IOptions<TOptions>, IOptionsSnapshot<TOptions>,
        IOptionsMonitor<TOptions>, IOptionsFactory<TOptions>, IFrozenStartupOptions where TOptions : class
    {
        public Type OptionsType => typeof(TOptions);
        object IFrozenStartupOptions.Instance => value;
        public TOptions Value => value;
        public TOptions CurrentValue => value;
        public TOptions Get(string? name) => string.IsNullOrEmpty(name) ? value
            : throw new InvalidOperationException("Startup configuration does not support named options.");
        public TOptions Create(string name) => Get(name);
        public IDisposable OnChange(Action<TOptions, string?> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            return EmptySubscription.Instance;
        }
    }

    private sealed class EmptySubscription : IDisposable
    {
        internal static readonly EmptySubscription Instance = new();
        public void Dispose() { }
    }
}

internal sealed record ConfigurationStartupEntry(Type OptionsType, ConfigurationDefinition Definition,
    object Instance, string Json, ConfigurationValidationReport ValidationReport);

internal sealed record ConfigurationStartupCandidateSnapshot(string Json, ConfigurationValidationReport ValidationReport,
    bool RequiresRestart, ConfigurationSnapshot Sources);
