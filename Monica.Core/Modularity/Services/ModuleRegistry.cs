using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Models.Internal;
using Monica.Core.Modularity.Services.Support;
using Monica.Core.Modularity.State;
using Monica.Core.TypeDiscovery.Services;
using Monica.Tool.Extensions;

namespace Monica.Core.Modularity.Services;

/// <summary>
/// Central registry that drives the module registration lifecycle and manages module configuration and initialization.
/// </summary>
public sealed class ModuleRegistry(MonicaApplication application)
{
    private const string CONFIGURE_BUILDER_AND_SERVICES_PHASE =
        nameof(ModulePhase.ConfigureBuilder) + " / " + nameof(ModulePhase.ConfigureServices);

    private readonly ModuleCompositionState _composition = new();
    private readonly object _compositionCallbackGate = new();
    private readonly object _diagnosticsGate = new();
    private readonly ModuleRegistryState _state = new();
    private readonly Dictionary<Type, HashSet<Type>> _hardDependencies = [];
    private readonly Dictionary<Type, HashSet<Type>> _optionalOrderings = [];
    private readonly Dictionary<Type, int> _registrationOrdinals = [];
    private int _nextRegistrationOrdinal;
    private Type? _activeDescriptorModuleType;
    private ModuleRegistrationState? _activeCompositionCallback;
    private int _activeCompositionCallbackThreadId;
    private ModuleStartupWorkScheduler? _startupWork;
    private TaskCompletionSource<string?>? _startupValidation;
    private bool _hasStarted;
    private bool _hasMutatedHost;
    private bool _isSealed;
    private CompiledModuleGraph? _compiledGraph;
    private ModuleServiceRegistrationWriter? _registrationWriter;
    private long _diagnosticsRevision;

    /// <summary>
    /// Gets a read-only view of registration errors owned by this Monica host.
    /// </summary>
    /// <remarks>
    /// The view follows the host lifecycle but cannot be used to mutate registry state.
    /// </remarks>
    internal IReadOnlyList<ModuleRegistrationError> RegistrationErrors => _state.RegistrationErrors;

    public ILogger Logger => application.CreateLogger(typeof(ModuleRegistry));

    /// <summary>
    /// Gets a read-only view of module snapshots captured after successful registration.
    /// </summary>
    /// <remarks>
    /// The view follows the host lifecycle but cannot be used to add or remove snapshots.
    /// </remarks>
    public IReadOnlyList<ModuleRuntimeSnapshot> RuntimeSnapshots => _state.RuntimeSnapshots;

    /// <summary>
    /// Registration information for every module type that has been registered.
    /// </summary>
    internal IReadOnlyDictionary<Type, ModuleRegistrationState> Registrations => _state.Registrations;

    /// <summary>
    /// Gets the immutable graph compiled for this host.
    /// </summary>
    internal CompiledModuleGraph CompiledGraph => _compiledGraph
        ?? throw new InvalidOperationException("The Monica module graph has not been compiled yet.");

    /// <summary>
    /// Gets whether composition crossed the boundary after which the host builder cannot be reused safely.
    /// </summary>
    internal bool HasMutatedHost => _hasMutatedHost;

    /// <summary>Gets the concrete host-builder type captured when composition began.</summary>
    internal Type? HostBuilderType => _composition.CaptureDiagnostics().HostBuilderType;

    /// <summary>
    /// Copies only registry references under short locks, projects immutable scalar state outside the lock, and
    /// retries when the registry revision changes before publication.
    /// </summary>
    internal ModuleRegistryDiagnosticsCapture CaptureDiagnostics()
    {
        while (true)
        {
            long revision;
            ModuleCompositionDiagnosticsState composition;
            ModuleRegistrationState[] registrations;
            ModuleRuntimeSnapshot[] runtimeModules;
            ModuleRegistrationError[] errors;
            lock (_diagnosticsGate)
            {
                revision = _diagnosticsRevision;
                composition = _composition.CaptureDiagnostics();
                registrations = _state.Registrations.Values.ToArray();
                runtimeModules = _state.RuntimeSnapshots.ToArray();
                errors = _state.RegistrationErrors.ToArray();
            }

            var detachedRegistrations = registrations.Select(static registration =>
                new ModuleRegistrationDiagnosticsState(
                    registration.ModuleType,
                    registration.ModulePhase,
                    registration.Order,
                    registration.ModuleSingleton.IsWebModule,
                    registration.RequiresWebHost,
                    registration.WebHostRequirementReason,
                    registration.DisabledReason)).ToImmutableArray();
            var detachedRuntimeModules = runtimeModules.Select(static snapshot =>
                new ModuleRuntimeDiagnosticsState(snapshot.ModuleType, snapshot.Order)).ToImmutableArray();
            var detachedErrors = errors.Select(static error =>
                new ModuleRegistrationErrorDiagnosticsState(
                    error.ModuleType,
                    error.ErrorType,
                    error.Phase,
                    error.WorkItemId)).ToImmutableArray();

            lock (_diagnosticsGate)
            {
                if (_diagnosticsRevision == revision)
                {
                    return new ModuleRegistryDiagnosticsCapture(
                        revision,
                        composition,
                        detachedRegistrations,
                        detachedRuntimeModules,
                        detachedErrors);
                }
            }
        }
    }

    /// <summary>Checks whether a detached registry observation still represents the latest visible state.</summary>
    internal bool IsDiagnosticsRevisionCurrent(long revision)
    {
        lock (_diagnosticsGate)
        {
            return _diagnosticsRevision == revision;
        }
    }

    /// <summary>Starts a module callback and updates its visible phase as one diagnostics transition.</summary>
    internal void StartModulePhase(
        ModuleRegistrationState registration,
        ModulePhase phase,
        ModuleCallbackKind kind,
        string? workItemId)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_diagnosticsGate)
        {
            application.Profiling.StartModulePhase(
                registration.ModuleType,
                application.Dependencies.ResolveModuleKey(registration.ModuleType),
                registration.Order,
                phase,
                kind,
                workItemId);
            registration.SetModulePhase(phase);
            RecordDiagnosticsMutationUnderLock();
        }
    }

    /// <summary>
    /// Gets registration drafts omitted from the executable graph by an explicit disable declaration or propagation.
    /// </summary>
    internal IReadOnlyList<ModuleRegistrationState> DisabledRegistrations => Registrations.Values
        .Where(static registration => registration.DisabledReason is not null)
        .OrderBy(registration => _registrationOrdinals[registration.ModuleType])
        .ToArray();

    internal bool TryGetRegistration(
        Type moduleType,
        [NotNullWhen(true)] out ModuleRegistrationState? registration)
    {
        return _state.TryGetRegistration(moduleType, out registration);
    }

    internal ModuleRegistrationState GetOrAddRegistration<TModule, TOptions>()
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        EnsureCompositionIsOpen();
        var registration = GetOrAddRegistrationCore<TModule, TOptions>();
        if (registration.IsFinalized && registration.ModuleSingleton is IApplicationCompositionModule)
            throw new InvalidOperationException("The application authority is finalized and cannot be declared or configured again.");
        return registration;
    }

    private ModuleRegistrationState GetOrAddRegistrationCore<TModule, TOptions>()
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        if (_state.TryGetRegistration(typeof(TModule), out var existing))
        {
            existing.Initialize<TModule, TOptions>();
            return existing;
        }

        var registration = new ModuleRegistrationState(application, typeof(TModule));
        registration.Initialize<TModule, TOptions>();
        if (!_state.TryAddRegistration(typeof(TModule), registration))
        {
            throw new ModuleRegistrationException($"Module type {typeof(TModule).FullName} is already registered.");
        }

        _registrationOrdinals.Add(typeof(TModule), _nextRegistrationOrdinal++);
        return registration;
    }

    internal void AddOptionContribution<TModule, TOptions>(Type? configuredBy, Action<TOptions> configure, Type? owner)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        GetOrAddRegistration<TModule, TOptions>().AddOptionContribution(configuredBy, configure, owner);
    }

    internal ModuleRegistration<TModule, TOptions> Require<TModule, TOptions>(
        Type ownerModuleType,
        Action<TOptions>? configure,
        Type? registrationOwner)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        EnsureRegistrationIsMutable(ownerModuleType);
        var dependency = GetOrAddRegistrationCore<TModule, TOptions>();
        AddEdge(_hardDependencies, ownerModuleType, typeof(TModule));
        if (configure is not null)
        {
            dependency.AddOptionContribution(ownerModuleType, configure, registrationOwner);
        }

        return new ModuleRegistration<TModule, TOptions>(application, ownerModuleType, registrationOwner);
    }

    internal void DescribeRequire<TModule, TOptions>(
        Type ownerModuleType,
        Action<TOptions>? configure)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        EnsureActiveDescriptor(ownerModuleType);
        var dependency = GetOrAddRegistrationCore<TModule, TOptions>();
        AddEdge(_hardDependencies, ownerModuleType, typeof(TModule));
        if (configure is not null)
        {
            dependency.AddOptionContribution(ownerModuleType, configure);
        }
    }

    internal void AfterIfPresent(Type ownerModuleType, Type dependencyModuleType)
    {
        EnsureRegistrationIsMutable(ownerModuleType);
        AddEdge(_optionalOrderings, ownerModuleType, dependencyModuleType);
    }

    internal void DescribeAfterIfPresent(Type ownerModuleType, Type dependencyModuleType)
    {
        EnsureActiveDescriptor(ownerModuleType);
        AddEdge(_optionalOrderings, ownerModuleType, dependencyModuleType);
    }

    internal void RequireFeature(Type moduleType, string featureName)
    {
        EnsureCompositionIsOpen();
        GetRegistration(moduleType).RequireFeature(featureName);
    }

    internal void SatisfyFeature(Type moduleType, string featureName)
    {
        EnsureCompositionIsOpen();
        GetRegistration(moduleType).SatisfyFeature(featureName);
    }

    internal void DescribeRequireFeature(Type moduleType, string featureName)
    {
        EnsureActiveDescriptor(moduleType);
        GetRegistration(moduleType).RequireFeature(featureName);
    }

    internal void DescribeRequireDependencyFeature<TModule, TOptions>(
        Type ownerModuleType,
        string featureName)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        EnsureActiveDescriptor(ownerModuleType);
        if (!_hardDependencies.TryGetValue(ownerModuleType, out var dependencies)
            || !dependencies.Contains(typeof(TModule)))
        {
            throw new InvalidOperationException(
                $"{ownerModuleType.Name} cannot require feature '{featureName}' from {typeof(TModule).Name} " +
                $"without first declaring Require<{typeof(TModule).Name}, {typeof(TOptions).Name}>().");
        }

        GetRegistration(typeof(TModule)).RequireFeature(featureName);
    }

    internal void Disable(Type moduleType, string reason)
    {
        EnsureCompositionIsOpen();
        GetRegistration(moduleType).MarkDisabled(reason);
    }

    internal void AddProfileContribution<TModule, TOptions>(string name, Action<TOptions> configure, Type? owner)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        GetOrAddRegistration<TModule, TOptions>().AddProfileContribution(name, configure, owner);
    }

    internal TOptions GetProfile<TModule, TOptions>(string name)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        return GetRegistration(typeof(TModule)).GetProfile<TOptions>(name);
    }

    internal TOptions GetRequiredOptions<TModule, TOptions>(Type sourceModuleType)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        if (!CompiledGraph.HardDependencies.TryGetValue(sourceModuleType, out var dependencies)
            || !dependencies.Contains(typeof(TModule)))
        {
            throw new InvalidOperationException(
                $"{sourceModuleType.Name} cannot read {typeof(TOptions).Name} without a direct Require<{typeof(TModule).Name}> edge.");
        }

        return (TOptions)GetRegistration(typeof(TModule)).ModuleOption;
    }

    internal bool TryGetOrderedOptions<TModule, TOptions>(Type sourceModuleType, out TOptions? options)
        where TModule : MonicaModule<TOptions>, new()
        where TOptions : ModuleOptions<TModule>, new()
    {
        var targetType = typeof(TModule);
        var isDeclared = CompiledGraph.HardDependencies.TryGetValue(sourceModuleType, out var required)
                         && required.Contains(targetType)
                         || CompiledGraph.OptionalOrderings.TryGetValue(sourceModuleType, out var ordered)
                         && ordered.Contains(targetType);
        if (!isDeclared)
        {
            throw new InvalidOperationException(
                $"{sourceModuleType.Name} cannot probe {typeof(TOptions).Name} without a direct Require or AfterIfPresent edge.");
        }

        if (!_state.TryGetRegistration(targetType, out var registration)
            || registration.DisabledReason is not null)
        {
            options = null;
            return false;
        }

        options = (TOptions)registration.ModuleOption;
        return true;
    }

    internal ModuleServiceRegistrationWriter GetRegistrationWriter(IServiceCollection services)
    {
        return _registrationWriter ??= new ModuleServiceRegistrationWriter(services);
    }

    /// <summary>
    /// Determines whether the current Monica host registered the specified module type.
    /// </summary>
    /// <param name="moduleType">The module type to inspect.</param>
    /// <returns><see langword="true"/> when the module belongs to the active compiled graph; otherwise, <see langword="false"/>.</returns>
    public bool IsRegistered(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        return _compiledGraph?.IsActive(moduleType) == true;
    }

    internal bool IsDisabled(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        return _state.TryGetRegistration(moduleType, out var registration)
               && registration.DisabledReason is not null;
    }

    /// <summary>
    /// Gets all keyed service keys registered by the specified module.
    /// </summary>
    /// <param name="moduleType">The module type.</param>
    /// <returns>The keyed service keys for the module, or an empty set if the module is unknown.</returns>
    public IReadOnlySet<string> GetKeyedServiceKeys(Type moduleType)
    {
        return TryGetRegistration(moduleType, out var info)
            ? info.KeyedServiceKeys.ToFrozenSet()
            : FrozenSet<string>.Empty;
    }

    /// <summary>
    /// Records an error produced by the host's module registration lifecycle.
    /// </summary>
    /// <param name="error">The error to record.</param>
    internal void AddRegistrationError(ModuleRegistrationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_diagnosticsGate)
        {
            _state.AddRegistrationError(error);
            RecordDiagnosticsMutationUnderLock();
        }
    }

    /// <summary>
    /// Registers services for all currently registered modules.
    /// This method must run before `builder.Build()`.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="monica">The Monica builder kept open through the application authority's registration pass.</param>
    [SuppressMessage("ReSharper", "PossibleMultipleEnumeration")]
    internal void RegisterServices(IHostApplicationBuilder builder, IMonicaBuilder monica)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_hasStarted)
        {
            throw new InvalidOperationException("The Monica module graph has already been applied to its host.");
        }

        _hasStarted = true;
        lock (_diagnosticsGate)
        {
            _composition.Initialize(builder);
            RecordDiagnosticsMutationUnderLock();
        }
        _startupWork = new ModuleStartupWorkScheduler(
            application.ModuleSystem.MaxConcurrentStartupWorkItems,
            OnStartupWorkCompleted,
            application.Profiling.RecordExternalMutation);
        application.Profiling.AttachStartupWorkDiagnostics(_startupWork.GetSnapshot);
        var services = builder.Services;
        CompiledTypeDiscovery? typeDiscovery = null;

        try
        {
            lock (_diagnosticsGate)
            {
                _state.ClearRegistrationErrors();
                RecordDiagnosticsMutationUnderLock();
            }
            DescribeModules();
            PrepareApplicationComposition(builder, monica);
            _isSealed = true;
            CompileGraph();
            ValidateFeatures();
            ValidateWebModuleCompatibility(builder);
            var registrations = MaterializeModules();
            DeclareModuleContracts(registrations);
            typeDiscovery = CompileTypeDiscovery(registrations);
            _hasMutatedHost = true;
            RegisterCoreServices(services);
            var snapshots = ExecuteBuilderAndServiceConfiguration(builder, services, registrations);
            ReachStartupWorkBarrier(ModuleStartupWorkBarrier.BeforeTypeDiscovery);
            application.FreezeTypeActivation();

            _registrationWriter = new ModuleServiceRegistrationWriter(services);
            try
            {
                CommitBusinessTypeDiscovery(builder, services, typeDiscovery);
            }
            finally
            {
                _registrationWriter = null;
                typeDiscovery.Release();
                typeDiscovery = null;
            }

            ReachStartupWorkBarrier(ModuleStartupWorkBarrier.BeforePostConfigureServices);

            ExecutePostConfigureServices(builder, services, snapshots);
            _startupWork.CloseSubmissions();
            ReachStartupWorkBarrier(ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion);
            application.Composition.FinalizeServices(services, () => ApplyHostedServiceActivation(services));
            ValidateServiceRequirements(services, registrations);

            lock (_diagnosticsGate)
            {
                _state.AddRuntimeSnapshots(snapshots);
                RecordDiagnosticsMutationUnderLock();
            }
            application.Errors.RaiseModuleErrors();
            application.Profiling.RecordMilestone(ModuleCompositionMilestone.ServiceRegistrationCompleted);
            if (builder is not WebApplicationBuilder)
            {
                CompleteComposition(ModuleCompositionCompletionPoint.ServiceRegistration);
            }
        }
        catch (Exception exception)
        {
            HandleRegistrationFailure(exception);
            throw;
        }
        finally
        {
            _registrationWriter = null;
            typeDiscovery?.Release();
        }
    }

    private void PrepareApplicationComposition(IHostApplicationBuilder builder, IMonicaBuilder monica)
    {
        PropagateDisabledModules();
        var authorities = Registrations.Values.Where(registration => registration.DisabledReason is null
            && registration.ModuleSingleton is IApplicationCompositionModule).ToArray();
        if (authorities.Length > 1)
            throw new InvalidOperationException("A host may declare only one application composition authority.");
        application.ResolveApplicationInputs();
        if (authorities.Length == 0)
        {
            application.FreezeApplicationInputs();
            return;
        }
        var registration = authorities[0];
        registration.StartModulePhase(ModulePhase.FinalizeOptions);
        try { registration.FinalizeOptions(); }
        finally { registration.EndModulePhase(ModulePhase.FinalizeOptions); }
        var authority = (IApplicationCompositionModule)registration.ModuleSingleton;
        application.PrepareApplication(() => authority.PrepareApplication(monica, builder));
        application.FreezeApplicationInputs();
        _hasMutatedHost = true;
        application.Composition.RegisterApplication(() => authority.RegisterApplication(monica, builder));
        DescribeModules();
        if (Registrations.Values.Any(candidate => candidate.ModuleSingleton is IApplicationCompositionModule
            && !ReferenceEquals(candidate, registration) && candidate.DisabledReason is null))
            throw new InvalidOperationException("Application registration cannot introduce another application composition authority.");
    }

    private void DescribeModules()
    {
        application.Profiling.StartPhase(nameof(ModulePhase.Describe));
        try
        {
            while (Registrations.Values
                       .Where(static registration => registration.ModulePhase == ModulePhase.None
                                                     && registration.DisabledReason is null)
                       .OrderBy(registration => _registrationOrdinals[registration.ModuleType])
                       .ToArray()
                   is { Length: > 0 } pending)
            {
                foreach (var info in pending)
                {
                    info.StartModulePhase(ModulePhase.Describe);
                    try
                    {
                        _activeDescriptorModuleType = info.ModuleType;
                        info.ModuleSingleton.Describe(new ModuleDescriptor(application, info.ModuleType));
                    }
                    finally
                    {
                        _activeDescriptorModuleType = null;
                        info.EndModulePhase(ModulePhase.Describe);
                    }
                }
            }
        }
        finally
        {
            application.Profiling.StopPhase(nameof(ModulePhase.Describe));
        }
    }

    private void ApplyHostedServiceActivation(IServiceCollection services)
    {
        if (application.TypeActivation is not { } activation) return;
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.IsKeyedService || descriptor.ServiceType != typeof(IHostedService)) continue;
            var owner = application.Composition.GetServiceRegistrationOwner(descriptor)
                ?? descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
            // Factory descriptors use their declaring owner without constructing their implementation.
            if (owner is not null && !activation.IsEnabled(owner,
                    Monica.Core.TypeDiscovery.Abstractions.TypeActivationKind.HostedService))
                services.RemoveAt(index);
        }
    }

    private void CompileGraph()
    {
        PropagateDisabledModules();

        _compiledGraph = ModuleGraphCompiler.Compile(
            Registrations,
            _hardDependencies,
            _optionalOrderings,
            _registrationOrdinals);
        for (var index = 0; index < _compiledGraph.ActiveModules.Count; index++)
        {
            GetRegistration(_compiledGraph.ActiveModules[index]).Order = 100 + index * 10;
        }
    }

    private void PropagateDisabledModules()
    {
        var reverseHardDependencies = Registrations.Keys.ToDictionary(
            static type => type,
            static _ => new List<Type>());
        foreach (var (owner, dependencies) in _hardDependencies)
        {
            foreach (var dependency in dependencies)
            {
                reverseHardDependencies[dependency].Add(owner);
            }
        }

        var queue = new Queue<Type>(Registrations.Values
            .Where(static registration => registration.DisabledReason is not null)
            .Select(static registration => registration.ModuleType));
        while (queue.TryDequeue(out var disabledType))
        {
            foreach (var dependentType in reverseHardDependencies[disabledType])
            {
                var dependent = GetRegistration(dependentType);
                if (dependent.DisabledReason is not null)
                {
                    continue;
                }

                dependent.MarkDisabled($"Required module {disabledType.Name} was disabled.");
                queue.Enqueue(dependentType);
            }
        }
    }

    private void ValidateFeatures()
    {
        var problems = Registrations.Values
            .Where(static registration => registration.DisabledReason is null)
            .OrderBy(static registration => registration.Order)
            .Select(registration => new
            {
                registration.ModuleType,
                Missing = registration.GetMissingRequiredFeatures(),
                Unexpected = registration.GetUnexpectedSatisfiedFeatures()
            })
            .Where(static result => result.Missing.Count != 0 || result.Unexpected.Count != 0)
            .ToArray();
        if (problems.Length == 0)
        {
            return;
        }

        var lines = new List<string>
        {
            "Module feature validation failed before option finalization:"
        };
        foreach (var problem in problems)
        {
            if (problem.Missing.Count != 0)
            {
                lines.Add(
                    $"- {problem.ModuleType.Name} is missing required features: " +
                    string.Join(", ", problem.Missing));
            }

            if (problem.Unexpected.Count != 0)
            {
                lines.Add(
                    $"- {problem.ModuleType.Name} satisfied undeclared features: " +
                    string.Join(", ", problem.Unexpected));
            }
        }

        throw new ModuleRegistrationException(string.Join(Environment.NewLine, lines));
    }

    private IReadOnlyList<ModuleRegistrationState> MaterializeModules()
    {
        var registrations = Registrations.Values
            .Where(static info => (info.ModulePhase == ModulePhase.Describe || info.IsFinalized)
                                  && info.DisabledReason is null)
            .OrderBy(static info => info.Order)
            .ToArray();

        application.Profiling.StartPhase(nameof(ModulePhase.FinalizeOptions));
        try
        {
            foreach (var info in registrations)
            {
                if (info.IsFinalized) continue;
                info.StartModulePhase(ModulePhase.FinalizeOptions);
                try
                {
                    info.FinalizeOptions();
                }
                catch (Exception exception)
                {
                    throw exception.CreateException(
                        Logger,
                        $"Module {info.ModuleType.GetCleanFullName()} failed during option finalization.");
                }
                finally
                {
                    info.EndModulePhase(ModulePhase.FinalizeOptions);
                }
            }
            return registrations;
        }
        finally
        {
            application.Profiling.StopPhase(nameof(ModulePhase.FinalizeOptions));
        }
    }

    private void DeclareModuleContracts(IReadOnlyList<ModuleRegistrationState> registrations)
    {
        application.Profiling.StartPhase(nameof(ModulePhase.DeclareContracts));
        try
        {
            foreach (var registration in registrations)
            {
                registration.StartModulePhase(ModulePhase.DeclareContracts);
                try
                {
                    registration.ModuleSingleton.DeclareModuleContracts(registration);
                }
                finally
                {
                    registration.EndModulePhase(ModulePhase.DeclareContracts);
                }
            }
        }
        finally
        {
            application.Profiling.StopPhase(nameof(ModulePhase.DeclareContracts));
        }
    }

    private static void ValidateServiceRequirements(
        IServiceCollection services,
        IReadOnlyList<ModuleRegistrationState> registrations)
    {
        var missing = registrations
            .SelectMany(registration => registration.ServiceRequirements.Select(requirement => new
            {
                Consumer = registration.ModuleType,
                Requirement = requirement
            }))
            .Where(item => !services.Any(descriptor =>
                descriptor.ServiceType == item.Requirement.ServiceType
                && descriptor.IsKeyedService == item.Requirement.IsKeyed
                && (!item.Requirement.IsKeyed
                    || Equals(descriptor.ServiceKey, item.Requirement.ServiceKey))))
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var lines = new List<string>
        {
            "Module service-contract validation failed after service registration:"
        };
        foreach (var item in missing)
        {
            var identity = item.Requirement.IsKeyed
                ? $"keyed {item.Requirement.ServiceType.Name} with key '{item.Requirement.ServiceKey}'"
                : $"unkeyed {item.Requirement.ServiceType.Name}";
            lines.Add($"- {item.Consumer.Name} requires {identity}, but no matching registration exists.");
        }

        throw new ModuleRegistrationException(string.Join(Environment.NewLine, lines));
    }

    private void RegisterCoreServices(IServiceCollection services)
    {
        services.AddSingleton<MonicaApplication>(_ => application);
        // Generic Host resolves HostOptions while it builds IHost. Make the host-owned Monica application an options
        // dependency so the container materializes and owns it even when the host is disposed without being started.
        services.AddOptions<HostOptions>()
            .Configure<MonicaApplication>(static (_, _) => { });
        services.AddSingleton<IMonicaApplicationOptions>(application.Application);
        services.AddSingleton<IMonicaModuleSystemOptions>(application.ModuleSystem);
        RegisterStartupValidation(services);

        foreach (var registration in Registrations.Values
                     .Where(static info => info.DisabledReason is null)
                     .OrderBy(static info => info.Order))
        {
            RegisterFrozenModuleOption(services, registration);
        }
    }

    private IReadOnlyList<ModuleRuntimeSnapshot> ExecuteBuilderAndServiceConfiguration(
        IHostApplicationBuilder builder,
        IServiceCollection services,
        IReadOnlyList<ModuleRegistrationState> registrations)
    {
        var snapshots = new List<ModuleRuntimeSnapshot>(registrations.Count);
        application.Profiling.StartPhase(CONFIGURE_BUILDER_AND_SERVICES_PHASE);
        try
        {
            foreach (var info in registrations)
            {
                ExecuteConfigurationRequests(builder, services, info, ModulePhase.ConfigureBuilder);
            }

            foreach (var info in registrations)
            {
                ExecuteConfigurationRequests(builder, services, info, ModulePhase.ConfigureServices);
                snapshots.Add(new ModuleRuntimeSnapshot(application, info.ModuleSingleton!, info));
            }

            return snapshots;
        }
        finally
        {
            application.Profiling.StopPhase(CONFIGURE_BUILDER_AND_SERVICES_PHASE);
        }
    }

    private void ExecuteConfigurationRequests(
        IHostApplicationBuilder builder,
        IServiceCollection services,
        ModuleRegistrationState registration,
        ModulePhase phase)
    {
        foreach (var request in registration.GetOrderedRequests(
                     registration.ConfigurationRequests.Where(request => request.Phase == phase)))
        {
            registration.StartModulePhase(phase, request.Kind);
            try
            {
                BeginCompositionCallback(registration);
                try
                {
                    application.Composition.RunOwned(request.RegistrationOwner, () => request.Configure(
                        new ModuleConfigurationContext(services, null, builder, registration, request.RegistrationOwner)));
                }
                finally
                {
                    EndCompositionCallback(registration);
                }
            }
            finally
            {
                registration.EndModulePhase(phase);
            }
        }
    }

    private void BeginCompositionCallback(ModuleRegistrationState registration)
    {
        lock (_compositionCallbackGate)
        {
            if (_activeCompositionCallback is not null)
            {
                throw new InvalidOperationException("Module composition callbacks cannot overlap on the serial control plane.");
            }

            _activeCompositionCallback = registration;
            _activeCompositionCallbackThreadId = Environment.CurrentManagedThreadId;
        }
    }

    private void EndCompositionCallback(ModuleRegistrationState registration)
    {
        lock (_compositionCallbackGate)
        {
            if (ReferenceEquals(_activeCompositionCallback, registration))
            {
                _activeCompositionCallback = null;
                _activeCompositionCallbackThreadId = 0;
            }
        }
    }

    private CompiledTypeDiscovery CompileTypeDiscovery(IReadOnlyList<ModuleRegistrationState> registrations)
    {
        var plans = new List<CompiledTypeDiscoveryPlan>(registrations.Count);
        TypeDiscoveryCompilation? compilation = null;
        try
        {
            application.Profiling.StartStage(ModuleSystemStage.TypeDiscoveryPlanDeclaration);
            try
            {
                foreach (var registration in registrations)
                {
                    registration.StartModulePhase(
                        ModulePhase.DeclareTypeDiscovery,
                        ModuleCallbackKind.Lifecycle);
                    try
                    {
                        var plan = registration.ModuleSingleton.DeclareTypeDiscoveryPlan();
                        if (plan.Registrations.Count == 0)
                        {
                            plan.Release();
                            continue;
                        }

                        plans.Add(new CompiledTypeDiscoveryPlan(registration, plan));
                    }
                    finally
                    {
                        registration.EndModulePhase(ModulePhase.DeclareTypeDiscovery);
                    }
                }
            }
            finally
            {
                application.Profiling.StopStage(ModuleSystemStage.TypeDiscoveryPlanDeclaration);
            }

            var assemblyCount = 0;
            application.Profiling.StartStage(ModuleSystemStage.TypeDiscoveryAssemblyResolution);
            try
            {
                if (plans.Count != 0)
                {
                    assemblyCount = application.TypeFinder.GetAssemblies().Count;
                }
            }
            finally
            {
                application.Profiling.StopStage(ModuleSystemStage.TypeDiscoveryAssemblyResolution);
            }

            IReadOnlyList<Type> businessTypes = [];
            application.Profiling.StartStage(ModuleSystemStage.TypeDiscoveryTypeEnumeration);
            try
            {
                if (plans.Count != 0)
                {
                    businessTypes = application.TypeFinder.GetTypes();
                }
            }
            finally
            {
                application.Profiling.StopStage(ModuleSystemStage.TypeDiscoveryTypeEnumeration);
            }

            application.Profiling.StartStage(ModuleSystemStage.TypeDiscoveryQueryEvaluation);
            try
            {
                compilation = TypeDiscoveryCompiler.Compile(
                    businessTypes,
                    plans.Select(static item => item.Plan).ToArray());
            }
            finally
            {
                application.Profiling.StopStage(ModuleSystemStage.TypeDiscoveryQueryEvaluation);
            }

            var querySummaries = compilation.Queries
                .Select((query, index) => new TypeDiscoveryQuerySummary
                {
                    QueryId = $"query-{index + 1:D4}",
                    ConsumerModules = plans
                        .Where(plan => plan.Plan.Registrations.Any(registration => registration.Query.Equals(query)))
                        .Select(plan => application.Dependencies.ResolveModuleKey(plan.Registration.ModuleType))
                        .Distinct()
                        .ToImmutableArray(),
                    MatchCount = compilation.GetMatches(query).Count
                })
                .ToArray();
            application.Profiling.RecordTypeDiscoveryCompilation(
                new TypeDiscoveryStatistics
                {
                    AssemblyCount = assemblyCount,
                    EnumeratedTypeCount = compilation.EnumeratedTypeCount,
                    ExcludedTypeCount = compilation.ExcludedTypeCount,
                    PlanCount = plans.Count,
                    DistinctQueryCount = compilation.Queries.Count,
                    MatchCount = compilation.MatchCount
                },
                querySummaries);

            return new CompiledTypeDiscovery(
                Array.AsReadOnly(plans.ToArray()),
                compilation);
        }
        catch (Exception exception)
        {
            foreach (var plan in plans)
            {
                plan.Plan.Release();
            }

            compilation?.Release();
            throw exception.CreateException(
                Logger,
                "Type-discovery plan compilation failed before host mutation.");
        }
    }

    private void CommitBusinessTypeDiscovery(
        IHostApplicationBuilder builder,
        IServiceCollection services,
        CompiledTypeDiscovery discovery)
    {
        var commitCallbackCount = 0;
        application.Profiling.StartStage(ModuleSystemStage.TypeDiscoveryRegistrationCommit);
        try
        {
            foreach (var discoveryPlan in discovery.Plans)
            {
                var registration = discoveryPlan.Registration;
                registration.StartModulePhase(
                    ModulePhase.DeclareTypeDiscovery,
                    ModuleCallbackKind.TypeDiscoveryCommit);
                try
                {
                    BeginCompositionCallback(registration);
                    try
                    {
                        discoveryPlan.Plan.Commit(
                            discovery.Compilation,
                            new ModuleConfigurationContext(
                                services,
                                applicationBuilder: null,
                                builder,
                                registration),
                            () => commitCallbackCount++);
                    }
                    finally
                    {
                        EndCompositionCallback(registration);
                    }
                }
                finally
                {
                    registration.EndModulePhase(ModulePhase.DeclareTypeDiscovery);
                }
            }
        }
        catch (Exception exception)
        {
            throw exception.CreateException(Logger, "Type-discovery commit failed during Monica module registration.");
        }
        finally
        {
            application.Profiling.RecordTypeDiscoveryCommit(
                commitCallbackCount,
                _registrationWriter?.GetStatistics() ?? new TypeDiscoveryServiceRegistrationStatistics());
            application.Profiling.StopStage(ModuleSystemStage.TypeDiscoveryRegistrationCommit);
        }
    }

    /// <summary>
    /// Clears all lifecycle data so an isolated host fixture can be rebuilt.
    /// </summary>
    internal void Clear()
    {
        DisposeStartupWork();
        _startupValidation = null;
        lock (_compositionCallbackGate)
        {
            _activeCompositionCallback = null;
            _activeCompositionCallbackThreadId = 0;
        }
        _activeDescriptorModuleType = null;
        lock (_diagnosticsGate)
        {
            _state.Clear();
            _composition.Clear();
            RecordDiagnosticsMutationUnderLock();
        }
        _hardDependencies.Clear();
        _optionalOrderings.Clear();
        _registrationOrdinals.Clear();
        _nextRegistrationOrdinal = 0;
        _compiledGraph = null;
        _registrationWriter = null;
        _hasStarted = false;
        _hasMutatedHost = false;
        _isSealed = false;
    }

    /// <summary>
    /// Rejects registration mutations after the owned application registration pass has completed.
    /// </summary>
    internal void EnsureCompositionIsOpen()
    {
        if (_isSealed)
        {
            throw new InvalidOperationException(
                "The Monica module graph is sealed. Register and configure modules only inside AddMonica(...).");
        }
    }

    internal void EnsureRegistrationIsMutable(Type moduleType)
    {
        EnsureCompositionIsOpen();
        var registration = GetRegistration(moduleType);
        if (registration.IsFinalized && registration.ModuleSingleton is IApplicationCompositionModule)
            throw new InvalidOperationException("The prepared application authority cannot be changed by owned registration.");
    }

    private void EnsureActiveDescriptor(Type ownerModuleType)
    {
        if (_activeDescriptorModuleType != ownerModuleType)
        {
            throw new InvalidOperationException(
                $"The descriptor for {ownerModuleType.Name} is valid only while that module's Describe callback is executing.");
        }
    }

    /// <summary>
    /// Validates module ownership and phase rules before handing isolated work to the scheduler.
    /// </summary>
    internal void ScheduleStartupWork(
        MonicaModule owner,
        string name,
        Action work,
        Action? commit,
        ModuleStartupWorkBarrier barrier)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);

        ValidateSynchronousCompositionDelegate(
            work,
            nameof(work),
            "Module startup work must be synchronous. Use the Generic Host lifecycle for asynchronous work.");
        if (commit is not null)
        {
            ValidateSynchronousCompositionDelegate(
                commit,
                nameof(commit),
                "Module startup work commits must be synchronous and cannot launch asynchronous work.");
        }

        if (!Enum.IsDefined(barrier))
        {
            throw new ArgumentOutOfRangeException(nameof(barrier), barrier, "Unknown startup work barrier.");
        }

        if (commit is not null
            && barrier is ModuleStartupWorkBarrier.BeforeHostLifecycle or ModuleStartupWorkBarrier.NoBarrier)
        {
            throw new InvalidOperationException(
                $"Startup work targeting {barrier} cannot have a serial commit because service registration " +
                "is sealed before that barrier.");
        }

        var moduleType = owner.GetType();
        if (!TryGetRegistration(moduleType, out var info)
            || !ReferenceEquals(info.ModuleSingleton, owner))
        {
            throw new InvalidOperationException(
                $"Only the materialized host-owned {moduleType.Name} instance can schedule startup work.");
        }

        lock (_compositionCallbackGate)
        {
            if (!ReferenceEquals(_activeCompositionCallback, info)
                || _activeCompositionCallbackThreadId != Environment.CurrentManagedThreadId
                || info.ModulePhase is not (ModulePhase.ConfigureBuilder
                    or ModulePhase.ConfigureServices
                    or ModulePhase.DeclareTypeDiscovery
                    or ModulePhase.PostConfigureServices))
            {
                throw new InvalidOperationException(
                    $"Module {moduleType.Name} can schedule startup work only while its synchronous " +
                    "ConfigureBuilder, ConfigureServices, DeclareTypeDiscovery, or PostConfigureServices " +
                    "callback is executing.");
            }

            if (info.ModulePhase == ModulePhase.DeclareTypeDiscovery
                && barrier == ModuleStartupWorkBarrier.BeforeTypeDiscovery)
            {
                throw new InvalidOperationException(
                    $"Module {moduleType.Name} cannot schedule startup work for " +
                    $"{ModuleStartupWorkBarrier.BeforeTypeDiscovery} during type discovery. " +
                    $"Use any later barrier: {ModuleStartupWorkBarrier.BeforePostConfigureServices}, " +
                    $"{ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion}, " +
                    $"{ModuleStartupWorkBarrier.BeforeHostLifecycle}, or {ModuleStartupWorkBarrier.NoBarrier}.");
            }
        }

        var scheduler = _startupWork
            ?? throw new InvalidOperationException("Module startup work is no longer available for this host.");
        var registrationOwner = application.Composition.RegistrationOwner;
        scheduler.Schedule(
            moduleType,
            application.Dependencies.ResolveModuleKey(moduleType),
            info.Order,
            name,
            info.ModulePhase,
            barrier,
            () => application.Composition.RunOwned(registrationOwner, work, captureServices: false),
            commit is null ? null : () => application.Composition.RunOwned(registrationOwner, commit));
    }

    private static void ValidateSynchronousCompositionDelegate(
        Action callback,
        string parameterName,
        string errorMessage)
    {
        if (callback.GetInvocationList().Any(static invocation =>
                invocation.Method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false)))
        {
            throw new ArgumentException(errorMessage, parameterName);
        }
    }

    private static void RegisterFrozenModuleOption(
        IServiceCollection services,
        ModuleRegistrationState registration)
    {
        var optionType = registration.ModuleOptionType;
        var option = registration.ModuleOption;
        var wrapperType = typeof(OptionsWrapper<>).MakeGenericType(optionType);
        var wrapper = Activator.CreateInstance(wrapperType, option)
            ?? throw new InvalidOperationException(
                $"Could not create the frozen options wrapper for {optionType.GetCleanFullName()}.");

        services.AddSingleton(optionType, option);
        services.AddSingleton(typeof(IOptions<>).MakeGenericType(optionType), wrapper);
    }

    private void RegisterStartupValidation(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<ModuleStartupValidationOptions>>(
            new ModuleStartupValidator(application));
        services.AddOptions<ModuleStartupValidationOptions>().ValidateOnStart();
        services.AddHostedService<MonicaApplicationLifecycle>();
    }

    private void ExecutePostConfigureServices(
        IHostApplicationBuilder builder,
        IServiceCollection services,
        IReadOnlyList<ModuleRuntimeSnapshot> snapshots)
    {
        application.Profiling.StartPhase(nameof(ModulePhase.PostConfigureServices));
        try
        {
            foreach (var module in snapshots)
            {
                ExecuteConfigurationRequests(
                    builder,
                    services,
                    module.RegisterInfo,
                    ModulePhase.PostConfigureServices);
            }
        }
        finally
        {
            application.Profiling.StopPhase(nameof(ModulePhase.PostConfigureServices));
        }
    }

    private void ReachStartupWorkBarrier(ModuleStartupWorkBarrier barrier)
    {
        var scheduler = _startupWork
            ?? throw new InvalidOperationException("Module startup work is unavailable for this host.");
        var release = scheduler.ReachBarrier(barrier);
        if (release.HasFailures)
        {
            var failures = release.WorkItems.Where(static result => !result.IsSucceeded).ToArray();
            foreach (var failure in failures)
            {
                application.Errors.RecordStartupWorkError(failure);
            }

            throw new ModuleStartupWorkFailureException(barrier, failures);
        }

        CommitStartupWork(release.WorkItems);
    }

    private void CommitStartupWork(IReadOnlyList<ModuleStartupWorkResult> workItems)
    {
        foreach (var result in workItems)
        {
            if (result.Commit?.Take() is not { } commit)
            {
                continue;
            }

            if (!TryGetRegistration(result.ModuleType, out var registration))
            {
                throw new InvalidOperationException(
                    $"Startup work '{result.Name}' cannot commit because module {result.ModuleType.FullName} " +
                    "is no longer registered.");
            }

            registration.StartModulePhase(
                result.OriginPhase,
                ModuleCallbackKind.StartupWorkCommit,
                result.WorkItemId);
            try
            {
                // The worker barrier is already released. This serial publication is intentionally profiled as a
                // repeated callback of the phase that scheduled the work rather than as worker wait time.
                commit();
            }
            catch (Exception exception)
            {
                application.Errors.RecordStartupWorkCommitError(result, exception);
                application.Errors.RaiseModuleErrors();
                throw;
            }
            finally
            {
                registration.EndModulePhase(result.OriginPhase);
            }
        }
    }

    private void HandleRegistrationFailure(Exception primaryFailure)
    {
        var scheduler = _startupWork;
        ModuleStartupWorkSnapshot snapshot = new(0, [], []);
        try
        {
            if (scheduler is not null)
            {
                scheduler.Drain();
                scheduler.ReleaseUncommittedCommits();
                snapshot = scheduler.GetSnapshot();
            }
        }
        catch (Exception drainFailure)
        {
            scheduler?.ReleaseUncommittedCommits();
            FailComposition(primaryFailure, ModuleCompositionFailureKind.ServiceRegistration);
            throw new AggregateException(
                "Monica composition failed and scheduled startup work could not be drained cleanly.",
                primaryFailure,
                drainFailure);
        }

        FailComposition(primaryFailure, ModuleCompositionFailureKind.ServiceRegistration);
        var workFailures = snapshot.WorkItems
            .Where(static result => result.Barrier != ModuleStartupWorkBarrier.NoBarrier && !result.IsSucceeded)
            .Select(static result => result.Failure!)
            .ToArray();
        if (workFailures.Length == 0)
        {
            return;
        }

        if (primaryFailure is ModuleStartupWorkFailureException or ModuleRegistrationException)
        {
            application.Errors.RaiseModuleErrors();
        }

        throw new AggregateException(
            "Monica composition and required scheduled startup work both failed.",
            [primaryFailure, .. workFailures]);
    }

    /// <summary>
    /// Claims the application builder before Monica configures the Web pipeline.
    /// </summary>
    internal void BeginApplicationPipeline(IApplicationBuilder app)
    {
        lock (_diagnosticsGate)
        {
            _composition.BeginApplicationPipeline(app);
            application.Profiling.RecordMilestone(ModuleCompositionMilestone.ApplicationPipelineStarted);
            RecordDiagnosticsMutationUnderLock();
        }
    }

    /// <summary>
    /// Records successful completion of the <c>UseMonica()</c> application-pipeline boundary.
    /// </summary>
    internal void CompleteApplicationPipeline()
    {
        lock (_diagnosticsGate)
        {
            application.Profiling.RecordMilestone(ModuleCompositionMilestone.ApplicationPipelineCompleted);
            RecordDiagnosticsMutationUnderLock();
        }
    }

    /// <summary>
    /// Claims the application builder before Monica maps Web endpoints.
    /// </summary>
    internal void BeginEndpointMapping(IApplicationBuilder app)
    {
        lock (_diagnosticsGate)
        {
            _composition.BeginEndpointMapping(app);
            application.Profiling.RecordMilestone(ModuleCompositionMilestone.EndpointMappingStarted);
            RecordDiagnosticsMutationUnderLock();
        }
    }

    /// <summary>
    /// Completes composition once at the host-appropriate boundary.
    /// </summary>
    internal void CompleteComposition(ModuleCompositionCompletionPoint completionPoint)
    {
        lock (_diagnosticsGate)
        {
            if (!_composition.TryBeginCompletion(completionPoint))
            {
                return;
            }

            RecordDiagnosticsMutationUnderLock();
        }

        try
        {
            application.Errors.RaiseModuleErrors();

            if (application.ModuleSystem.EnableSummaryLog)
            {
                Logger.LogInformation("Module system performance summary:\n{PerformanceSummary}",
                    application.Profiling.GetPerformanceSummary());
                Logger.LogInformation("Module system register order summary:\n{Order}",
                    application.Dependencies.GetModuleRegistrationSummary());
            }

            lock (_diagnosticsGate)
            {
                _composition.CommitCompletion();
                application.Profiling.RecordMilestone(ModuleCompositionMilestone.CompositionCompleted);
                application.Profiling.StopModuleSystem();
                RecordDiagnosticsMutationUnderLock();
            }
        }
        catch (Exception exception)
        {
            FailComposition(exception, ModuleCompositionFailureKind.Completion);
            throw;
        }
    }

    /// <summary>
    /// Gets a host-start failure when the Web pipeline has not completed Monica composition.
    /// </summary>
    internal string? GetStartupValidationFailure()
    {
        if (_composition.GetStartupValidationFailure() is { } compositionFailure)
        {
            FailComposition(
                new InvalidOperationException(compositionFailure),
                ModuleCompositionFailureKind.StartupValidation);
            return compositionFailure;
        }

        var candidate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validation = Interlocked.CompareExchange(ref _startupValidation, candidate, null);
        if (validation is not null)
        {
            return validation.Task.GetAwaiter().GetResult();
        }

        try
        {
            ReachStartupWorkBarrier(ModuleStartupWorkBarrier.BeforeHostLifecycle);
            candidate.TrySetResult(null);
        }
        catch (Exception exception)
        {
            candidate.TrySetResult(exception.GetMessageRecursively());
        }

        return candidate.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Drains host-owned non-blocking work after the Generic Host has stopped.
    /// </summary>
    internal void DrainStartupWork()
    {
        _startupWork?.Drain();
    }

    /// <summary>
    /// Drains and releases the startup-work scheduler during application disposal.
    /// </summary>
    internal void DisposeStartupWork()
    {
        var scheduler = Interlocked.Exchange(ref _startupWork, null);
        scheduler?.Dispose();
    }

    private void OnStartupWorkCompleted(ModuleStartupWorkResult result)
    {
        if (result.Barrier == ModuleStartupWorkBarrier.NoBarrier && !result.IsSucceeded)
        {
            Logger.LogError(
                result.Failure,
                "Non-blocking startup work {WorkName} owned by module {ModuleType} failed. " +
                "Host startup is unaffected; inspect module-system diagnostics for details.",
                result.Name,
                result.ModuleType.FullName);
        }
    }

    private ModuleRegistrationState GetRegistration(Type moduleType)
    {
        return _state.TryGetRegistration(moduleType, out var registration)
            ? registration
            : throw new KeyNotFoundException($"Module {moduleType.FullName} is not registered in this host.");
    }

    private void RecordDiagnosticsMutationUnderLock()
    {
        _diagnosticsRevision++;
        application.Profiling.RecordExternalMutation();
    }

    private static void AddEdge(
        IDictionary<Type, HashSet<Type>> graph,
        Type ownerModuleType,
        Type dependencyModuleType)
    {
        if (ownerModuleType == dependencyModuleType)
        {
            throw new ModuleRegistrationException(
                $"Module {ownerModuleType.Name} cannot depend on itself.");
        }

        if (!graph.TryGetValue(ownerModuleType, out var dependencies))
        {
            dependencies = [];
            graph.Add(ownerModuleType, dependencies);
        }

        dependencies.Add(dependencyModuleType);
    }

    /// <summary>
    /// Configures the application pipeline for the registered modules.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="stage">The named routing boundary being configured.</param>
    internal void ConfigApplicationPipeline(IApplicationBuilder app, ModuleWebStage stage)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown web lifecycle stage.");
        }

        var phaseName = $"{nameof(ConfigApplicationPipeline)}_{stage}";
        application.Profiling.StartPhase(phaseName);

        try
        {
            foreach (var module in RuntimeSnapshots.Where(p =>
                         p.RegisterInfo.ModuleSingleton.IsWebModule &&
                         p.RegisterInfo.ModulePhase is (ModulePhase.PostConfigureServices or ModulePhase.ConfigureApplicationBuilder)))
            {
                foreach (var request in module.RegisterInfo.GetOrderedRequests(
                             module.RegisterInfo.ConfigurationRequests
                                 .Where(p => p.Phase == ModulePhase.ConfigureApplicationBuilder)
                                 .Where(request => request.WebStage == stage)))
                {
                    module.RegisterInfo.StartModulePhase(
                        ModulePhase.ConfigureApplicationBuilder,
                        request.Kind);
                    try
                    {
                        BeginCompositionCallback(module.RegisterInfo);
                        try
                        {
                            application.Composition.RunOwned(request.RegistrationOwner, () => request.Configure(
                                new ModuleConfigurationContext(null, app, null, module.RegisterInfo, request.RegistrationOwner)),
                                captureServices: false);
                        }
                        finally
                        {
                            EndCompositionCallback(module.RegisterInfo);
                        }
                    }
                    finally
                    {
                        module.RegisterInfo.EndModulePhase(ModulePhase.ConfigureApplicationBuilder);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FailComposition(exception, ModuleCompositionFailureKind.ApplicationPipeline);
            throw;
        }
        finally
        {
            application.Profiling.StopPhase(phaseName);
        }
    }

    /// <summary>
    /// Configures endpoints for the registered modules.
    /// </summary>
    /// <param name="app">The application builder.</param>
    internal void ConfigEndpoints(IApplicationBuilder app)
    {
        application.Profiling.StartPhase(nameof(ModulePhase.ConfigureEndpoints));

        try
        {
            foreach (var module in RuntimeSnapshots.Where(p =>
                         p.RegisterInfo.ModuleSingleton.IsWebModule &&
                         p.RegisterInfo.ModulePhase == ModulePhase.ConfigureApplicationBuilder))
            {
                foreach (var request in module.RegisterInfo.GetOrderedRequests(
                             module.RegisterInfo.ConfigurationRequests
                                 .Where(p => p.Phase == ModulePhase.ConfigureEndpoints)))
                {
                    module.RegisterInfo.StartModulePhase(ModulePhase.ConfigureEndpoints, request.Kind);
                    try
                    {
                        BeginCompositionCallback(module.RegisterInfo);
                        try
                        {
                            application.Composition.RunOwned(request.RegistrationOwner, () => request.Configure(
                                new ModuleConfigurationContext(null, app, null, module.RegisterInfo, request.RegistrationOwner)),
                                captureServices: false);
                        }
                        finally
                        {
                            EndCompositionCallback(module.RegisterInfo);
                        }
                    }
                    finally
                    {
                        module.RegisterInfo.EndModulePhase(ModulePhase.ConfigureEndpoints);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            FailComposition(exception, ModuleCompositionFailureKind.EndpointMapping);
            throw;
        }
        finally
        {
            application.Profiling.StopPhase(nameof(ModulePhase.ConfigureEndpoints));
        }

        CompleteComposition(ModuleCompositionCompletionPoint.EndpointMapping);
    }

    private void FailComposition(Exception exception, ModuleCompositionFailureKind failureKind)
    {
        lock (_diagnosticsGate)
        {
            _composition.FailCompletion(exception, failureKind);
            application.Profiling.StopModuleSystem();
            RecordDiagnosticsMutationUnderLock();
        }
    }

    /// <summary>
    /// Gets all module snapshots that are providers for a specific target module.
    /// </summary>
    /// <param name="targetModuleType">The target module strategy type.</param>
    /// <returns>A detached list of runtime snapshots for modules that provide the target module.</returns>
    public IReadOnlyList<ModuleRuntimeSnapshot> GetModuleProviders(Type targetModuleType)
    {
        return Array.AsReadOnly(RuntimeSnapshots
            .Where(snapshot => snapshot.ModuleInstance is IModuleProvider provider
                               && provider.ProvidesFor == targetModuleType)
            .ToArray());
    }

    /// <summary>
    /// Gets all module providers of a specific type for a target module.
    /// </summary>
    /// <typeparam name="TProvider">The provider interface type</typeparam>
    /// <param name="targetModuleType">The target module strategy type.</param>
    /// <returns>A detached, read-only list of provider instances.</returns>
    public IReadOnlyList<TProvider> GetModuleProviders<TProvider>(Type targetModuleType)
        where TProvider : class, IModuleProvider
    {
        return Array.AsReadOnly(RuntimeSnapshots
            .Select(static snapshot => snapshot.ModuleInstance)
            .OfType<TProvider>()
            .Where(provider => provider.ProvidesFor == targetModuleType)
            .ToArray());
    }

    private void ValidateWebModuleCompatibility(IHostApplicationBuilder builder)
    {
        if (builder is WebApplicationBuilder)
        {
            return;
        }

        var requiredModules = Registrations.Values
            .Where(static registration => registration.DisabledReason is null
                                          && registration.RequiresWebHost)
            .OrderBy(static registration => registration.Order)
            .Select(static registration =>
                $"{registration.ModuleType.FullName ?? registration.ModuleType.Name}: " +
                registration.WebHostRequirementReason)
            .ToArray();
        if (requiredModules.Length == 0)
        {
            return;
        }

        throw new ModuleRegistrationException(
            $"The generic host adapter {builder.GetType().FullName} cannot compose modules that require an " +
            $"ASP.NET Core WebApplicationBuilder: {string.Join(", ", requiredModules)}.");
    }
}
