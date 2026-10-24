using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Configuration;

namespace Monica.Core;

/// <summary>Owns one host's frozen startup sources and framework composition extension state.</summary>
/// <remarks>
/// Ordinary sources must be supplied before AddMonica. Framework extensions share this instance without building
/// a service provider. State factories and validation callbacks run synchronously during composition.
/// </remarks>
public sealed class MonicaCompositionState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, object> _states = [];
    private readonly HashSet<Type> _creatingStates = [];
    private readonly List<ServiceFinalization> _finalizers = [];
    private readonly Dictionary<int, Type?> _registrationOwners = [];
    private readonly Dictionary<ServiceDescriptor, Type?> _serviceOwners = new(ReferenceEqualityComparer.Instance);
    private readonly EnvironmentFileConfiguration _environmentFiles = new();
    private bool _servicesFinalized;
    private bool _finalizingServices;
    private ConfigurationSnapshot? _startup;
    private IServiceCollection? _services;
    private IList<IConfigurationSource>? _sources;
    private bool _applicationRegistrationsOpen;
    private bool _runningApplicationRegistration;
    private int _applicationRegistrationThread;

    /// <summary>Gets the declaring application type for the current framework contribution, or null for unowned composition.</summary>
    /// <remarks>
    /// Framework registration APIs capture this origin per resource or deferred contribution. It is host-owned and
    /// restored explicitly for synchronous callbacks; it must not become a shared module's single owner.
    /// </remarks>
    public Type? RegistrationOwner
    {
        get
        {
            lock (_gate) return _registrationOwners.GetValueOrDefault(Environment.CurrentManagedThreadId);
        }
    }

    internal MonicaCompositionState(IConfiguration configuration) => LiveConfiguration = configuration;

    /// <summary>Gets the immutable provider snapshot captured before the AddMonica callback.</summary>
    public ConfigurationSnapshot StartupConfiguration => _startup
        ?? throw new InvalidOperationException("Startup configuration has not been captured yet.");

    /// <summary>Gets the host's live configuration, including accepted reloads and later module projections.</summary>
    public IConfiguration LiveConfiguration { get; }

    /// <summary>Gets environment-file acceptance and rejection diagnostics without configuration values.</summary>
    public IReadOnlyList<EnvironmentFileConfigurationDiagnostic> EnvironmentFileDiagnostics => _environmentFiles.Diagnostics;

    internal bool HasMutatedHost { get; private set; }

    internal void Initialize(IHostApplicationBuilder builder, Action<string> reportError)
    {
        _services = builder.Services;
        _sources = builder.Configuration.Sources;
        if (builder.Configuration is not IConfigurationRoot root)
            throw new InvalidOperationException("Monica startup inputs require a host configuration manager exposing IConfigurationRoot provider provenance.");
        ShapeAwareJsonConfigurationExtensions.PreserveJsonShapes(builder.Configuration);
        _environmentFiles.Load(builder, () => HasMutatedHost = true, reportError);
        _environmentFiles.CaptureOrigins(root);
        _startup = ConfigurationSnapshot.Capture(root);
    }

    /// <summary>Executes one domain or role registration with its declaring type as the contribution owner.</summary>
    /// <remarks>
    /// Only the application authority may call this during its pre-seal registration pass. The callback is
    /// synchronous, cannot recursively run another owner, and cannot change configuration source selection.
    /// Service descriptors and deferred framework contributions retain this origin without resolving factories.
    /// </remarks>
    public void RunRegistration(Type owner, Action callback)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!_applicationRegistrationsOpen || _runningApplicationRegistration
                || _applicationRegistrationThread != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Owned registration is valid only in the application's single, nonrecursive registration pass.");
            _runningApplicationRegistration = true;
        }
        try { RunOwned(owner, callback); }
        finally
        {
            lock (_gate) _runningApplicationRegistration = false;
        }
    }

    internal void RegisterApplication(Action callback)
    {
        var sources = CaptureSourceSelection();
        lock (_gate)
        {
            if (_applicationRegistrationsOpen)
                throw new InvalidOperationException("Application registration cannot execute recursively.");
            _applicationRegistrationsOpen = true;
            _applicationRegistrationThread = Environment.CurrentManagedThreadId;
        }
        try
        {
            callback();
            if (!sources.SequenceEqual(CaptureSourceSelection()))
                throw new InvalidOperationException("Application registrations cannot change frozen configuration source selection.");
        }
        finally
        {
            lock (_gate)
            {
                _applicationRegistrationsOpen = false;
                _applicationRegistrationThread = 0;
            }
        }
    }

    private SourceSelection[] CaptureSourceSelection() => _sources?.Select(source => new SourceSelection(source,
        (source as FileConfigurationSource)?.Path, (source as FileConfigurationSource)?.FileProvider)).ToArray() ?? [];

    internal void RunOwned(Type? owner, Action callback, bool captureServices = true)
    {
        var thread = Environment.CurrentManagedThreadId;
        Type? previous;
        bool hadPrevious;
        lock (_gate)
        {
            hadPrevious = _registrationOwners.TryGetValue(thread, out previous);
            _registrationOwners[thread] = owner;
        }
        var services = captureServices ? _services : null;
        var before = services is null ? null : new HashSet<ServiceDescriptor>(services, ReferenceEqualityComparer.Instance);
        try { callback(); }
        finally
        {
            lock (_gate)
            {
                if (before is not null)
                    foreach (var descriptor in services!)
                        if (!before.Contains(descriptor)) _serviceOwners.TryAdd(descriptor, owner);
                if (hadPrevious) _registrationOwners[thread] = previous;
                else _registrationOwners.Remove(thread);
            }
        }
    }

    internal Type? GetServiceRegistrationOwner(ServiceDescriptor descriptor)
    {
        lock (_gate) return _serviceOwners.GetValueOrDefault(descriptor);
    }

    /// <summary>Returns the one framework state of this type, creating it once for this host.</summary>
    /// <remarks>
    /// Creation is allowed only before service finalization; existing state remains readable afterward. The factory
    /// must not resolve services or recursively create its own type. Failed creation is not cached. Extensions own
    /// any resources in their state; registering the same resource in DI does not transfer a second disposal here.
    /// </remarks>
    public TState GetOrAdd<TState>(Func<TState> factory) where TState : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_states.TryGetValue(typeof(TState), out var existing)) return (TState)existing;
            EnsureOpen();
            if (!_creatingStates.Add(typeof(TState)))
                throw new InvalidOperationException($"Composition state '{typeof(TState).FullName}' cannot recursively create itself.");
            try
            {
                var state = factory() ?? throw new InvalidOperationException("A composition-state factory returned null.");
                _states.Add(typeof(TState), state);
                return state;
            }
            finally { _creatingStates.Remove(typeof(TState)); }
        }
    }

    /// <summary>Registers final service completion and an optional invariant check after all module registration barriers.</summary>
    /// <remarks>
    /// Completion callbacks run once in registration order and may register finalized framework adapters. All
    /// completions finish before validation callbacks receive the same read-only descriptor snapshot. Validators
    /// must not mutate registrations. Neither phase may resolve services, declare modules or register further hooks.
    /// Later user mutations after AddMonica returns are outside this validation boundary.
    /// </remarks>
    /// <param name="complete">Completes framework-owned service descriptors without resolving services.</param>
    /// <param name="validate">Checks the final descriptor snapshot after every completion callback, or null for no check.</param>
    public void OnServicesFinalized(Action<IServiceCollection> complete,
        Action<IReadOnlyList<ServiceDescriptor>>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(complete);
        lock (_gate)
        {
            EnsureOpen();
            _finalizers.Add(new ServiceFinalization(complete, validate, RegistrationOwner));
        }
    }

    /// <summary>Validates every selected environment file now and every later candidate before it is admitted.</summary>
    /// <remarks>
    /// The owning configuration module installs one final inventory check during composition. Validate each raw
    /// contribution independently; values hidden by other providers still belong to that file. Throw
    /// <see cref="ConfigurationSourcePathException"/> to report a rejected path; arbitrary exception messages are
    /// concealed. Rejected reloads retain both values and shapes from the last accepted revision.
    /// </remarks>
    public void ValidateEnvironmentFiles(Action<ConfigurationProviderSnapshot> validate)
    {
        lock (_gate)
        {
            if (_servicesFinalized)
                throw new InvalidOperationException("Environment-file validation must be installed during composition.");
            _environmentFiles.Validate(validate);
        }
    }

    internal void FinalizeServices(IServiceCollection services, Action applyActivation)
    {
        ServiceFinalization[] callbacks;
        lock (_gate)
        {
            EnsureOpen();
            _finalizingServices = true;
            callbacks = _finalizers.ToArray();
            _finalizers.Clear();
        }
        try
        {
            foreach (var callback in callbacks) RunOwned(callback.Owner, () => callback.Complete(services));
            applyActivation();
            var descriptors = Array.AsReadOnly(services.ToArray());
            foreach (var callback in callbacks)
                if (callback.Validate is { } validate) RunOwned(callback.Owner, () => validate(descriptors), captureServices: false);
        }
        finally
        {
            lock (_gate)
            {
                _finalizingServices = false;
                _servicesFinalized = true;
            }
        }
    }

    private void EnsureOpen()
    {
        if (_servicesFinalized || _finalizingServices)
            throw new InvalidOperationException("Framework composition state is closed after service finalization.");
    }

    private sealed record ServiceFinalization(Action<IServiceCollection> Complete,
        Action<IReadOnlyList<ServiceDescriptor>>? Validate, Type? Owner);

    private sealed record SourceSelection(IConfigurationSource Source, string? Path,
        Microsoft.Extensions.FileProviders.IFileProvider? FileProvider);
}
