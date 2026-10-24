using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Monica.Core.Logging;
using Monica.Core.Configuration;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Services;
using Monica.Core.Modularity.Services.Support;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.Core.TypeDiscovery.Services;

namespace Monica.Core;

/// <summary>
/// Owns the configuration, module graph, lifecycle state, and diagnostics for one Monica host.
/// </summary>
/// <remarks>
/// The host creates this context through <c>AddMonica</c> and registers the same instance in dependency injection.
/// Monica does not use process-global or async-flow ambient registration state.
/// </remarks>
public sealed class MonicaApplication : IDisposable
{
    private readonly MonicaApplicationOptions _applicationOptions = new();
    private readonly MonicaModuleSystemOptions _moduleSystemOptions = new();
    private readonly List<ILoggerFactory> _ownedCompositionLoggerFactories = [];
    private ILoggerFactory _compositionLoggerFactory;
    private bool _disposed;
    private TypeFinderOptions _typeFinderOptions = new();
    private ITypeDependencyOrderer? _typeDependencyOrderer;
    private ITypeFinder? _typeFinder;
    private bool _typeActivationFrozen;
    private MonicaCompositionState? _composition;
    private bool _applicationInputsFrozen;
    private bool _preparingApplication;

    internal MonicaApplication(MonicaStartupOrigin? startupOrigin = null)
    {
        _compositionLoggerFactory = CreateBootstrapLoggerFactory();
        _ownedCompositionLoggerFactories.Add(_compositionLoggerFactory);
        Dependencies = new ModuleDependencyAnalyzer(this);
        Profiling = new ModuleInitializationProfiler(startupOrigin);
        Modules = new ModuleRegistry(this);
        Errors = new ModuleErrorRegistry(this);
    }

    /// <summary>
    /// Gets application identity defaults for this host.
    /// </summary>
    public IMonicaApplicationOptions Application => _applicationOptions;

    /// <summary>Gets the frozen startup input and framework state owned by this host's composition.</summary>
    public MonicaCompositionState Composition => _composition
        ?? throw new InvalidOperationException("This application has not been attached to a host builder.");

    internal bool CompositionHasMutatedHost => _composition?.HasMutatedHost == true;

    internal void InitializeComposition(IHostApplicationBuilder builder)
    {
        if (_composition is not null) throw new InvalidOperationException("Composition inputs have already been initialized.");
        _composition = new MonicaCompositionState(builder.Configuration);
        var logger = CreateLogger<MonicaCompositionState>();
        _composition.Initialize(builder, error => logger.LogWarning("{EnvironmentFileDiagnostic}", error));
    }

    /// <summary>Gets the optional immutable business activation policy installed during composition.</summary>
    public ITypeActivationPolicy? TypeActivation { get; private set; }

    /// <summary>Installs the activation policy before business discovery commits.</summary>
    /// <remarks>The application-model module owns the policy; replacing it would invalidate committed registrations.</remarks>
    public void SetTypeActivationPolicy(ITypeActivationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (_typeActivationFrozen)
            throw new InvalidOperationException("Business activation is frozen after service configuration.");
        if (TypeActivation is not null)
            throw new InvalidOperationException("The business activation policy has already been installed.");
        TypeActivation = policy;
    }

    internal void FreezeTypeActivation() => _typeActivationFrozen = true;

    /// <summary>
    /// Gets module-system defaults for this host.
    /// </summary>
    public IMonicaModuleSystemOptions ModuleSystem => _moduleSystemOptions;

    internal MonicaApplicationOptions ApplicationConfiguration => _applicationOptions;

    /// <summary>Projects the selected deployment identity during the application authority's preparation callback.</summary>
    /// <remarks>
    /// The AppId must match the canonical startup identity. An explicitly configured AppName must agree with the
    /// selected deployment entry. This method cannot be called by owned registrations or after preparation.
    /// </remarks>
    public void ProjectApplicationIdentity(string appId, string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        if (!_preparingApplication)
            throw new InvalidOperationException("Deployment identity can only be projected while preparing the application authority.");
        ResolveStartupIdentity("Application:AppId", _applicationOptions.AppId);
        if (!string.Equals(Application.ResolveAppId(), appId, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected deployment AppId conflicts with the canonical application identity.");
        if (MonicaApplicationOptions.Normalize(Application.AppName) is { } configuredName
            && !string.Equals(configuredName, appName, StringComparison.Ordinal))
            throw new InvalidOperationException("The selected deployment AppName conflicts with explicitly configured application identity.");
        _applicationOptions.AppId = appId;
        _applicationOptions.AppName = appName;
    }

    internal void ResolveApplicationInputs()
    {
        _applicationOptions.AppId = ResolveStartupIdentity("Application:AppId", _applicationOptions.AppId);
        _applicationOptions.DeploymentScope = ResolveStartupIdentity("Application:DeploymentScope", _applicationOptions.DeploymentScope);
    }

    private string? ResolveStartupIdentity(string path, string? configured)
    {
        var selected = Composition.StartupConfiguration.SelectCompleteSection(path);
        if (selected is null) return MonicaApplicationOptions.Normalize(configured);
        var section = selected.GetSection(path);
        if (!selected.TryGetShape(path, out var shape) || shape != ConfigurationValueShape.Scalar
            || section.GetChildren().Any() || MonicaApplicationOptions.Normalize(section.Value) is not { } value)
            throw new InvalidOperationException($"'{path}' must be a nonempty scalar startup value.");
        if (MonicaApplicationOptions.Normalize(configured) is { } explicitValue
            && !string.Equals(explicitValue, value, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{path}' conflicts with explicitly configured application identity.");
        return value;
    }

    internal void PrepareApplication(Action prepare)
    {
        if (_preparingApplication || _applicationInputsFrozen)
            throw new InvalidOperationException("Application preparation can run only once before inputs are frozen.");
        _preparingApplication = true;
        try { prepare(); }
        finally { _preparingApplication = false; }
    }

    internal void FreezeApplicationInputs()
    {
        ResolveApplicationInputs();
        _applicationOptions.Freeze();
        _typeFinderOptions.Freeze();
        _applicationInputsFrozen = true;
    }

    internal MonicaModuleSystemOptions ModuleSystemConfiguration => _moduleSystemOptions;

    /// <summary>
    /// Gets the business-type finder configured for this host.
    /// </summary>
    public ITypeFinder TypeFinder => _typeFinder ??= new DomainTypeFinder(
        _typeFinderOptions,
        CreateLogger<DomainTypeFinder>());

    /// <summary>
    /// Gets the host-bound service that orders selected business types by their assembly dependencies.
    /// </summary>
    /// <remarks>
    /// The service builds one immutable dependency graph from this host's configured type-discovery assemblies. Modules
    /// opt into ordering only their own selected subsets; the shared business-type stream remains unchanged.
    /// </remarks>
    public ITypeDependencyOrderer TypeDependencyOrderer =>
        _typeDependencyOrderer ??= new TypeDependencyOrderer(TypeFinder.GetAssemblies());

    /// <summary>
    /// Gets the host-bound module registry and its read-only runtime views.
    /// </summary>
    public ModuleRegistry Modules { get; }

    /// <summary>
    /// Gets the host-bound module dependency analyzer.
    /// </summary>
    public ModuleDependencyAnalyzer Dependencies { get; }

    /// <summary>
    /// Gets the immutable application startup observation, or <see langword="null"/> when the host did not supply a
    /// <see cref="MonicaStartup"/> marker.
    /// </summary>
    public MonicaStartupTiming? StartupTiming => Profiling.GetApplicationStartupTiming();

    internal ModuleInitializationProfiler Profiling { get; }

    internal ModuleErrorRegistry Errors { get; }

    /// <summary>
    /// Replaces business-type discovery settings while the host-bound composition is still being declared.
    /// </summary>
    /// <param name="configure">An optional callback that customizes discovery.</param>
    internal void ConfigureTypeDiscovery(Action<TypeFinderOptions>? configure = null)
    {
        if (_applicationInputsFrozen)
            throw new InvalidOperationException("Type discovery is frozen before owned registration begins.");
        var options = new TypeFinderOptions();
        configure?.Invoke(options);
        _typeFinderOptions = options;
        _typeFinder = null;
        _typeDependencyOrderer = null;
    }

    internal ILogger<T> CreateLogger<T>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _compositionLoggerFactory.CreateLogger<T>();
    }

    internal ILogger CreateLogger(Type categoryType)
    {
        ArgumentNullException.ThrowIfNull(categoryType);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _compositionLoggerFactory.CreateLogger(categoryType);
    }

    internal ILogger<T> CreateLogger<T>(LogLevel minimumLevel)
    {
        return new MinimumLevelLogger<T>(CreateLogger<T>(), minimumLevel);
    }

    internal void ReplaceCompositionLoggerFactory(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ReferenceEquals(_compositionLoggerFactory, factory))
        {
            return;
        }

        _compositionLoggerFactory = factory;
        if (!_ownedCompositionLoggerFactories.Any(ownedFactory =>
                ReferenceEquals(ownedFactory, factory)))
        {
            _ownedCompositionLoggerFactories.Add(factory);
        }
    }

    internal void ResetModuleState()
    {
        Modules.Clear();
        Dependencies.Clear();
        Profiling.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Startup workers may still use the host's composition logger while non-blocking work drains.
        Modules.DisposeStartupWork();
        _disposed = true;
        for (var index = _ownedCompositionLoggerFactories.Count - 1; index >= 0; index--)
        {
            _ownedCompositionLoggerFactories[index].Dispose();
        }

        _ownedCompositionLoggerFactories.Clear();
    }

    private static ILoggerFactory CreateBootstrapLoggerFactory()
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddConsole();
        });
    }
}
