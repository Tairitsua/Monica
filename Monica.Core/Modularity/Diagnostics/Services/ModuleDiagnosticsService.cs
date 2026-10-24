using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services.Support;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Models.Internal;
using Monica.Core.Modularity.State;
using Monica.Core.TypeDiscovery.Models;
using Monica.Tool.Extensions;
using Monica.Modules;

namespace Monica.Core.Modularity.Diagnostics.Services;

/// <summary>
/// Projects one immutable, revisioned diagnostics view from the host-owned module composition state.
/// </summary>
internal sealed class ModuleDiagnosticsService
{
    private const int MAX_FAILURE_LENGTH = 512;
    private readonly MonicaApplication _application;
    private readonly ModuleSystemOption _options;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly object _snapshotGate = new();
    private readonly string _compositionId = Guid.NewGuid().ToString("N");
    private readonly Lazy<TypeDiscoveryAssemblyInventory> _assemblyInventory;
    private readonly ModuleOptionDiagnosticsProjector _optionProjector = new();
    private readonly ConcurrentDictionary<
        OptionDiagnosticsCacheKey,
        Lazy<ModuleOptionDiagnostics>> _redactedOptions = new();
    private ModuleDiagnosticsSnapshot? _cachedSnapshot;
    private long _cachedRevision = -1;
    private Action? _optionProjectionStarting;
    private Action? _snapshotProjectionStarting;
    private ModuleDiagnosticsSnapshot? _terminalSnapshot;

    public ModuleDiagnosticsService(
        MonicaApplication application,
        IOptions<ModuleSystemOption> options,
        IHostEnvironment hostEnvironment)
    {
        _application = application;
        _options = options.Value;
        _hostEnvironment = hostEnvironment;
        _assemblyInventory = new Lazy<TypeDiscoveryAssemblyInventory>(
            CreateAssemblyInventory,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Installs an internal observation hook used to coordinate deterministic publication-race tests after source
    /// capture and before projection. Production composition leaves this hook unset.
    /// </summary>
    internal void SetSnapshotProjectionObserver(Action? observer) =>
        Volatile.Write(ref _snapshotProjectionStarting, observer);

    /// <summary>
    /// Installs an internal observation hook used to verify that concurrent redacted requests share one projection.
    /// Production composition leaves this hook unset.
    /// </summary>
    internal void SetOptionProjectionObserver(Action? observer) =>
        Volatile.Write(ref _optionProjectionStarting, observer);

    /// <summary>Gets the current observation, retaining one stable reference after startup becomes terminal.</summary>
    internal ModuleDiagnosticsSnapshot GetSnapshot()
    {
        var terminal = Volatile.Read(ref _terminalSnapshot);
        if (terminal is not null)
        {
            return terminal;
        }

        var source = CaptureSource();
        lock (_snapshotGate)
        {
            if (_terminalSnapshot is not null)
            {
                return _terminalSnapshot;
            }

            if (_cachedRevision == source.Revision && _cachedSnapshot is not null)
            {
                return _cachedSnapshot;
            }
        }

        // Project only immutable captured data; no profiler, registry, or scheduler lock is held here.
        Volatile.Read(ref _snapshotProjectionStarting)?.Invoke();
        var projected = ProjectSnapshot(source);
        // Module work can be terminal before the host starts. For an explicitly tracked application, delay successful
        // reference freezing until readiness so the terminal instance includes the startup duration. Untracked hosts
        // freeze immediately; composition failures cannot reach ApplicationStarted and are also safe to freeze.
        var canFreezeTerminalSnapshot = projected.IsFinal
                                        && (source.Composition.IsFailed
                                            || source.Performance.ApplicationStartup is null
                                            || source.Performance.ApplicationStartup.IsReady);
        lock (_snapshotGate)
        {
            if (_terminalSnapshot is not null)
            {
                return _terminalSnapshot;
            }

            if (_cachedRevision > source.Revision && _cachedSnapshot is not null)
            {
                return _cachedSnapshot;
            }

            if (_cachedRevision == source.Revision && _cachedSnapshot is not null)
            {
                return _cachedSnapshot;
            }

            _cachedRevision = source.Revision;
            _cachedSnapshot = projected;
            var snapshot = projected;
            if (canFreezeTerminalSnapshot)
            {
                _terminalSnapshot = snapshot;
            }

            return snapshot;
        }
    }

    /// <summary>Gets the lazily constructed assembly inventory for this host.</summary>
    internal TypeDiscoveryAssemblyInventory GetAssemblyInventory() => _assemblyInventory.Value;

    internal IReadOnlyList<ResourceActivationObservation> GetResourceActivations() => Array.AsReadOnly(
        _application.ActivationResources.Select(resource => new ResourceActivationObservation
        {
            Key = resource.Key, ImplementationTypeName = resource.ImplementationType?.FullName,
            RegistrationOwnerTypeName = resource.RegistrationOwner?.FullName,
            Kind = resource.Kind, Mode = resource.Mode, Reason = resource.Reason
        }).ToArray());

    /// <summary>Gets the public option-property catalog with bounded values for one active module and profile.</summary>
    internal ModuleOptionDiagnostics GetModuleOptions(
        ModuleKey moduleKey,
        ModuleOptionProfileSelector? selector = null)
    {
        selector ??= ModuleOptionProfileSelector.Default;
        if (!_application.Dependencies.ModuleTypesByKey.TryGetValue(moduleKey, out var moduleType)
            || !_application.Modules.Registrations.TryGetValue(moduleType, out var registration))
        {
            throw new KeyNotFoundException($"Module '{moduleKey}' is not part of this composition.");
        }

        var exposureMode = _application.ModuleSystem.OptionDiagnosticsExposureMode;
        switch (exposureMode)
        {
            case ModuleOptionDiagnosticsExposureMode.Redacted:
                break;
            case ModuleOptionDiagnosticsExposureMode.RevealSensitive when !_hostEnvironment.IsDevelopment():
                throw new InvalidOperationException(
                    "Sensitive module option diagnostics are available only in the Development environment.");
            case ModuleOptionDiagnosticsExposureMode.RevealSensitive:
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported module option diagnostics exposure mode '{exposureMode}'.");
        }

        var optionTypeName = registration.ModuleOptionType.GetCleanFullName();
        if (!registration.IsFinalized)
        {
            return new ModuleOptionDiagnostics
            {
                ModuleKey = moduleKey,
                OptionTypeName = optionTypeName,
                RequestedProfileName = selector.ProfileName,
                ExposureMode = exposureMode
            };
        }

        var resolved = ResolveOption(registration, selector);

        ModuleOptionDiagnostics ProjectOptions()
        {
            Volatile.Read(ref _optionProjectionStarting)?.Invoke();
            return _optionProjector.Project(
                moduleKey,
                selector.ProfileName,
                resolved.ProfileName,
                resolved.Resolution,
                registration.ModuleOptionType,
                resolved.Options,
                exposureMode,
                _options.GetOptionDiagnosticsPolicy(moduleType));
        }

        if (exposureMode == ModuleOptionDiagnosticsExposureMode.RevealSensitive)
        {
            return ProjectOptions();
        }

        var cacheKey = new OptionDiagnosticsCacheKey(moduleKey, selector.Mode, selector.ProfileName);
        var cached = _redactedOptions.GetOrAdd(
            cacheKey,
            _ => new Lazy<ModuleOptionDiagnostics>(
                ProjectOptions,
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return cached.Value;
        }
        catch
        {
            if (_redactedOptions.TryGetValue(cacheKey, out var current)
                && ReferenceEquals(current, cached))
            {
                _redactedOptions.TryRemove(cacheKey, out _);
            }

            throw;
        }
    }

    /// <summary>Creates a portable export with process-local keys and sensitive details removed.</summary>
    internal ModuleDiagnosticsExport CreateExport()
    {
        var snapshot = GetSnapshot();
        return new ModuleDiagnosticsExport
        {
            SchemaVersion = ModuleDiagnosticsSnapshot.CURRENT_SCHEMA_VERSION,
            CompositionId = snapshot.CompositionId,
            Revision = snapshot.Revision,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            IsFinal = snapshot.IsFinal,
            Outcome = snapshot.Outcome,
            ApplicationName = snapshot.Host.ApplicationName,
            ApplicationVersion = snapshot.Host.ApplicationVersion,
            Summary = snapshot.Summary,
            TypeDiscovery = new ModuleDiagnosticsExportTypeDiscovery
            {
                Statistics = snapshot.TypeDiscovery.Statistics,
                Stages = snapshot.TypeDiscovery.Stages,
                Queries = snapshot.TypeDiscovery.Queries.Select(query =>
                    new ModuleDiagnosticsExportQuerySummary
                    {
                        QueryId = query.QueryId,
                        ConsumerModuleIds = query.ConsumerModules
                            .Select(static module => module.Id)
                            .ToImmutableArray(),
                        MatchCount = query.MatchCount
                }).ToImmutableArray()
            },
            Modules = snapshot.Modules.Select(static module => new ModuleDiagnosticsExportModule
            {
                ModuleId = module.ModuleKey.Id,
                TypeName = module.TypeName,
                AssemblyName = module.AssemblyName,
                RegistrationOrder = module.RegistrationOrder,
                IsActive = module.IsActive,
                DependencyDepth = module.DependencyDepth,
                SerialCallbackDurationMs = module.SerialCallbackDurationMs,
                StartupWorkDurationMs = module.StartupWorkDurationMs
            }).ToImmutableArray(),
            Edges = snapshot.Topology.Edges.Select(static edge =>
                new ModuleDiagnosticsExportDependencyEdge
                {
                    SourceModuleId = edge.SourceModule.Id,
                    TargetModuleId = edge.TargetModule.Id
                }).ToImmutableArray(),
            TraceSpans = snapshot.TraceSpans.Select(static span => new ModuleDiagnosticsExportTraceSpan
            {
                SpanId = span.SpanId,
                Kind = span.Kind,
                ModuleId = span.ModuleKey?.Id,
                SystemStage = span.SystemStage,
                ModulePhase = span.ModulePhase,
                CallbackKind = span.CallbackKind,
                StartedOffsetMs = span.StartedOffsetMs,
                EndedOffsetMs = span.EndedOffsetMs
            }).ToImmutableArray(),
            Findings = snapshot.Findings.Select(static finding => new ModuleDiagnosticsExportFinding
            {
                Code = finding.Code,
                Severity = finding.Severity,
                Evidence = finding.Evidence,
                RelatedModuleIds = finding.RelatedModules.Select(static module => module.Id).ToImmutableArray(),
                RelatedSpanIds = finding.RelatedSpanIds
            }).ToImmutableArray()
        };
    }

    private static ResolvedModuleOption ResolveOption(
        ModuleRegistrationState registration,
        ModuleOptionProfileSelector selector)
    {
        if (selector.Mode == ModuleOptionProfileSelectionMode.Default)
        {
            return new ResolvedModuleOption(
                registration.ModuleOption,
                null,
                ModuleOptionProfileResolution.Default);
        }

        var profileName = selector.ProfileName
            ?? throw new InvalidOperationException("A named module option selector requires a profile name.");
        if (registration.TryGetProfile(profileName, out var profile))
        {
            return new ResolvedModuleOption(
                profile,
                profileName,
                ModuleOptionProfileResolution.Named);
        }

        if (selector.Mode == ModuleOptionProfileSelectionMode.NamedOrDefault)
        {
            return new ResolvedModuleOption(
                registration.ModuleOption,
                null,
                ModuleOptionProfileResolution.DefaultFallback);
        }

        throw new KeyNotFoundException(
            $"Named option profile '{profileName}' was not declared for {registration.ModuleType.Name}.");
    }

    private ModuleDiagnosticsSource CaptureSource()
    {
        while (true)
        {
            var registry = _application.Modules.CaptureDiagnostics();
            var profiling = _application.Profiling.CaptureDiagnostics();

            // Projection intentionally runs after both producer locks have been released. The revision handshake
            // below rejects the detached projection if either producer changed while scalar values were copied.
            var dependencies = _application.Dependencies.DependenciesByModule.ToImmutableDictionary(
                static entry => entry.Key,
                static entry => entry.Value.ToImmutableHashSet());
            var topologicalOrder = _application.Dependencies.GetModulesInDependencyOrder().ToImmutableArray();

            if (!_application.Modules.IsDiagnosticsRevisionCurrent(registry.Revision)
                || _application.Profiling.GetRevision() != profiling.Revision)
            {
                continue;
            }

            return new ModuleDiagnosticsSource(
                profiling.Revision,
                registry.Composition,
                profiling.Performance,
                profiling.TypeDiscoveryStatistics,
                profiling.TypeDiscoveryQueries,
                registry.Registrations,
                registry.RuntimeModules,
                registry.Errors,
                dependencies,
                topologicalOrder);
        }
    }

    private readonly record struct OptionDiagnosticsCacheKey(
        ModuleKey ModuleKey,
        ModuleOptionProfileSelectionMode SelectionMode,
        string? ProfileName);

    private sealed record ResolvedModuleOption(
        object Options,
        string? ProfileName,
        ModuleOptionProfileResolution Resolution);

    private ModuleDiagnosticsSnapshot ProjectSnapshot(ModuleDiagnosticsSource source)
    {
        var traceSpans = CreateTraceSpans(source.Performance);
        var typeDiscovery = CreateTypeDiscovery(source, traceSpans);
        var topology = CreateTopology(source.Dependencies, source.TopologicalOrder);
        var modules = CreateModules(source, topology);
        var blockingChain = CreateBlockingChain(source.Performance, traceSpans);
        var summary = CreateSummary(source, typeDiscovery);
        var findings = CreateFindings(source, summary, traceSpans);
        var startupWorkTerminal = source.Performance.StartupWorkItems.All(static work =>
            work.Status is ModuleStartupWorkStatus.Succeeded or ModuleStartupWorkStatus.Failed);
        var isFinal = source.Composition.IsTerminal && startupWorkTerminal;
        var hasFailures = source.Composition.IsFailed
                          || source.Errors.Length != 0
                          || source.Performance.StartupWorkItems.Any(static work =>
                              work.Status == ModuleStartupWorkStatus.Failed);
        ModuleCompositionOutcome? outcome = !isFinal
            ? null
            : hasFailures
                ? ModuleCompositionOutcome.Failed
                : findings.Any(static finding => finding.Severity == ModuleDiagnosticFindingSeverity.Warning)
                    ? ModuleCompositionOutcome.Degraded
                    : ModuleCompositionOutcome.Succeeded;

        return new ModuleDiagnosticsSnapshot
        {
            CompositionId = _compositionId,
            Revision = source.Revision,
            StartedAtUtc = source.Performance.StartedAtUtc,
            CapturedAtUtc = source.Performance.ObservedAtUtc,
            IsFinal = isFinal,
            Outcome = outcome,
            Host = new ModuleDiagnosticsHostContext
            {
                ApplicationName = _application.Application.AppName ?? _hostEnvironment.ApplicationName,
                ApplicationVersion = _application.Application.AppVersion,
                EnvironmentName = _hostEnvironment.EnvironmentName,
                HostKind = source.Composition.HostBuilderType?.Name
            },
            Summary = summary,
            TraceSpans = traceSpans,
            TypeDiscovery = typeDiscovery,
            Modules = modules,
            Topology = topology,
            BlockingChain = blockingChain,
            Findings = findings
        };
    }

    private ModuleDiagnosticsSummary CreateSummary(
        ModuleDiagnosticsSource source,
        TypeDiscoveryDiagnostics discovery)
    {
        var typeDiscoveryDurationMs = discovery.Stages.Sum(static stage => stage.DurationMs);
        var longestCallbackDurationMs = source.Performance.ModulePhaseExecutions
            .Select(static execution => execution.DurationMs)
            .DefaultIfEmpty()
            .Max();
        var longestQueueDurationMs = source.Performance.StartupWorkItems
            .Select(static work => work.QueueDurationMs)
            .DefaultIfEmpty()
            .Max();
        var summary = new ModuleDiagnosticsSummary
        {
            ModuleCount = source.Registrations.Length,
            ActiveModuleCount = source.RuntimeModules.Length,
            DisabledModuleCount = source.Registrations.Count(static registration =>
                registration.DisabledReason is not null),
            ActiveStartupWorkCount = source.Performance.StartupWorkItems.Count(static work =>
                work.Status is ModuleStartupWorkStatus.Queued or ModuleStartupWorkStatus.Running),
            ErrorCount = source.Errors.Length + (source.Composition.IsFailed && source.Errors.Length == 0 ? 1 : 0),
            TotalCompositionDurationMs = source.Performance.Initialization.TotalDurationMs,
            ApplicationStartupDurationMs = source.Performance.ApplicationStartup?.DurationMs,
            ServiceRegistrationDurationMs = source.Performance.ServiceRegistrationDurationMs,
            TypeDiscoveryDurationMs = typeDiscoveryDurationMs,
            AggregateBarrierWaitDurationMs = source.Performance.AggregateBarrierWaitDurationMs,
            LongestModuleCallbackDurationMs = longestCallbackDurationMs,
            LongestStartupQueueDurationMs = longestQueueDurationMs
        };
        return summary with { PerformanceBudgets = EvaluateBudgets(summary) };
    }

    private ImmutableArray<ModulePerformanceBudgetEvaluation> EvaluateBudgets(ModuleDiagnosticsSummary summary)
    {
        var budgets = _application.ModuleSystem.StartupPerformanceBudgets;
        var evaluations = new List<ModulePerformanceBudgetEvaluation>(6);
        if (budgets is null)
        {
            return [];
        }

        Add(ModulePerformanceBudgetKind.TotalComposition, summary.TotalCompositionDurationMs, budgets.TotalComposition);
        Add(ModulePerformanceBudgetKind.ServiceRegistration, summary.ServiceRegistrationDurationMs, budgets.ServiceRegistration);
        Add(ModulePerformanceBudgetKind.TypeDiscovery, summary.TypeDiscoveryDurationMs, budgets.TypeDiscovery);
        Add(ModulePerformanceBudgetKind.AggregateBarrierWait, summary.AggregateBarrierWaitDurationMs, budgets.AggregateBarrierWait);
        Add(ModulePerformanceBudgetKind.LongestModuleCallback, summary.LongestModuleCallbackDurationMs, budgets.LongestModuleCallback);
        Add(ModulePerformanceBudgetKind.LongestStartupQueue, summary.LongestStartupQueueDurationMs, budgets.LongestStartupQueue);
        return evaluations.ToImmutableArray();

        void Add(ModulePerformanceBudgetKind kind, double actualDurationMs, TimeSpan? limit)
        {
            if (limit is null)
            {
                return;
            }

            evaluations.Add(new ModulePerformanceBudgetEvaluation
            {
                Kind = kind,
                ActualDurationMs = actualDurationMs,
                LimitDurationMs = limit.Value.TotalMilliseconds
            });
        }
    }

    private static TypeDiscoveryDiagnostics CreateTypeDiscovery(
        ModuleDiagnosticsSource source,
        ImmutableArray<ModuleDiagnosticsTraceSpan> traceSpans)
    {
        var stages = traceSpans
            .Where(static span => span.SystemStage is >= ModuleSystemStage.TypeDiscoveryPlanDeclaration
                and <= ModuleSystemStage.TypeDiscoveryRegistrationCommit)
            .Select(static span => new TypeDiscoveryStageMetric
            {
                Stage = span.SystemStage!.Value,
                SpanId = span.SpanId,
                DurationMs = span.DurationMs
            })
            .ToImmutableArray();
        return new TypeDiscoveryDiagnostics
        {
            Statistics = source.TypeDiscoveryStatistics,
            Stages = stages,
            Queries = source.TypeDiscoveryQueries
        };
    }

    private static ImmutableArray<ModuleDiagnosticsTraceSpan> CreateTraceSpans(
        ModuleCompositionPerformance performance)
    {
        var spans = new List<ModuleDiagnosticsTraceSpan>();
        spans.AddRange(performance.SystemPhases.Select(phase =>
            new ModuleDiagnosticsTraceSpan
            {
                SpanId = phase.ExecutionId,
                Kind = ModuleDiagnosticsTraceSpanKind.SystemStage,
                SystemStage = phase.Stage,
                Name = phase.Stage is null ? phase.PhaseName : null,
                StartedAtUtc = phase.StartedAtUtc,
                CompletedAtUtc = phase.CompletedAtUtc,
                StartedOffsetMs = phase.StartedOffsetMs,
                EndedOffsetMs = phase.CompletedOffsetMs
            }));
        spans.AddRange(performance.ModulePhaseExecutions.Select(execution =>
            new ModuleDiagnosticsTraceSpan
            {
                SpanId = execution.ExecutionId,
                Kind = ModuleDiagnosticsTraceSpanKind.ModuleCallback,
                ModuleKey = execution.ModuleKey,
                WorkItemId = execution.WorkItemId,
                ModulePhase = execution.Phase,
                CallbackKind = execution.Kind,
                StartedAtUtc = execution.StartedAtUtc,
                CompletedAtUtc = execution.CompletedAtUtc,
                StartedOffsetMs = execution.StartedOffsetMs,
                EndedOffsetMs = execution.CompletedOffsetMs
            }));
        spans.AddRange(performance.StartupWorkItems.Select(work =>
            new ModuleDiagnosticsTraceSpan
            {
                SpanId = $"startup-work-{work.Sequence:D6}",
                Kind = ModuleDiagnosticsTraceSpanKind.StartupWork,
                ModuleKey = work.ModuleKey,
                WorkItemId = work.WorkItemId,
                ModulePhase = work.OriginPhase,
                Barrier = work.Barrier,
                Name = work.Name,
                StartedAtUtc = work.StartedAtUtc ?? work.SubmittedAtUtc,
                CompletedAtUtc = work.CompletedAtUtc,
                StartedOffsetMs = work.StartedOffsetMs ?? work.SubmittedOffsetMs,
                EndedOffsetMs = work.CompletedOffsetMs ?? performance.ObservedDurationMs,
                QueueDurationMs = work.QueueDurationMs,
                IsFailed = work.Status == ModuleStartupWorkStatus.Failed
            }));
        spans.AddRange(performance.StartupWorkBarriers.Select(barrier =>
            new ModuleDiagnosticsTraceSpan
            {
                SpanId = $"startup-barrier-{barrier.Sequence:D6}",
                Kind = ModuleDiagnosticsTraceSpanKind.StartupBarrier,
                WorkItemId = barrier.ReleasingWorkItemId,
                Barrier = barrier.Barrier,
                StartedAtUtc = barrier.EnteredAtUtc,
                CompletedAtUtc = barrier.ReleasedAtUtc,
                StartedOffsetMs = barrier.EnteredOffsetMs,
                EndedOffsetMs = barrier.ReleasedOffsetMs
            }));
        return spans
            .OrderBy(static span => span.StartedOffsetMs)
            .ThenBy(static span => span.EndedOffsetMs)
            .ThenBy(static span => span.Kind)
            .ThenBy(static span => span.SpanId, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static ModuleDiagnosticsTopology CreateTopology(
        ImmutableDictionary<ModuleKey, ImmutableHashSet<ModuleKey>> dependencies,
        ImmutableArray<ModuleKey> topologicalOrder)
    {
        var depths = new Dictionary<ModuleKey, int>();
        foreach (var module in topologicalOrder)
        {
            var direct = dependencies.GetValueOrDefault(module) ?? ImmutableHashSet<ModuleKey>.Empty;
            depths[module] = direct.Count == 0
                ? 0
                : direct.Where(depths.ContainsKey).Select(dependency => depths[dependency] + 1).DefaultIfEmpty().Max();
        }

        return new ModuleDiagnosticsTopology
        {
            Edges = dependencies
                .SelectMany(static entry => entry.Value.Select(target => new ModuleDiagnosticsDependencyEdge
                {
                    SourceModule = entry.Key,
                    TargetModule = target
                }))
                .OrderBy(static edge => edge.SourceModule.Id, StringComparer.Ordinal)
                .ThenBy(static edge => edge.TargetModule.Id, StringComparer.Ordinal)
                .ToImmutableArray(),
            TopologicalOrder = topologicalOrder,
            MaximumDepth = depths.Values.DefaultIfEmpty().Max()
        };
    }

    private static ImmutableArray<ModuleDiagnosticsModule> CreateModules(
        ModuleDiagnosticsSource source,
        ModuleDiagnosticsTopology topology)
    {
        var runtimeByType = source.RuntimeModules.ToDictionary(static snapshot => snapshot.ModuleType);
        var performanceByKey = source.Performance.ModulePhaseExecutions.GroupBy(static execution => execution.ModuleKey)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var workByKey = source.Performance.StartupWorkItems.GroupBy(static work => work.ModuleKey)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var dependentCounts = topology.Edges.GroupBy(static edge => edge.TargetModule)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var depthByKey = new Dictionary<ModuleKey, int>();
        foreach (var key in topology.TopologicalOrder)
        {
            var dependencies = source.Dependencies.GetValueOrDefault(key) ?? ImmutableHashSet<ModuleKey>.Empty;
            depthByKey[key] = dependencies.Count == 0
                ? 0
                : dependencies.Where(depthByKey.ContainsKey)
                    .Select(dependency => depthByKey[dependency] + 1)
                    .DefaultIfEmpty()
                    .Max();
        }

        return source.Registrations
            .OrderBy(static registration => registration.DisabledReason is null ? registration.Order : int.MaxValue)
            .ThenBy(static registration => registration.ModuleType.FullName, StringComparer.Ordinal)
            .Select(registration =>
            {
                runtimeByType.TryGetValue(registration.ModuleType, out var runtime);
                var key = ModuleKey.FromModuleType(registration.ModuleType);
                performanceByKey.TryGetValue(key, out var executions);
                workByKey.TryGetValue(key, out var workItems);
                return new ModuleDiagnosticsModule
                {
                    ModuleKey = key,
                    TypeName = registration.ModuleType.Name,
                    FullTypeName = registration.ModuleType.GetCleanFullName(),
                    AssemblyName = registration.ModuleType.Assembly.GetName().Name ?? string.Empty,
                    RegistrationOrder = runtime?.Order,
                    Phase = registration.ModulePhase,
                    IsActive = runtime is not null,
                    IsUiModule = typeof(IUIModule).IsAssignableFrom(registration.ModuleType),
                    IsWebModule = registration.IsWebModule,
                    RequiresWebHost = registration.RequiresWebHost,
                    WebHostRequirementReason = registration.WebHostRequirementReason,
                    DisabledReason = registration.DisabledReason,
                    DependencyDepth = depthByKey.GetValueOrDefault(key),
                    DirectDependencyCount = source.Dependencies.GetValueOrDefault(key)?.Count ?? 0,
                    DirectDependentCount = dependentCounts.GetValueOrDefault(key),
                    SerialCallbackDurationMs = executions?.Sum(static execution => execution.DurationMs) ?? 0,
                    StartupWorkDurationMs = workItems?.Sum(static work => work.ExecutionDurationMs) ?? 0,
                    ErrorCount = source.Errors.Count(error => error.ModuleType == registration.ModuleType)
                };
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<ModuleBlockingChainSegment> CreateBlockingChain(
        ModuleCompositionPerformance performance,
        ImmutableArray<ModuleDiagnosticsTraceSpan> traceSpans)
    {
        var workById = performance.StartupWorkItems.ToDictionary(static work => work.WorkItemId, StringComparer.Ordinal);
        return performance.StartupWorkBarriers
            .SelectMany(barrier => barrier.PendingWorkItems.Select((pending, index) => (barrier, pending, index)))
            .Where(item => workById.ContainsKey(item.pending.WorkItemId))
            .Select(item =>
            {
                var work = workById[item.pending.WorkItemId];
                var commitSpan = traceSpans.FirstOrDefault(span =>
                    span.Kind == ModuleDiagnosticsTraceSpanKind.ModuleCallback
                    && span.CallbackKind == ModuleCallbackKind.StartupWorkCommit
                    && string.Equals(span.WorkItemId, work.WorkItemId, StringComparison.Ordinal));
                return new ModuleBlockingChainSegment
                {
                    SegmentId = $"blocking-{item.barrier.Sequence:D6}-{item.index:D3}",
                    BarrierSpanId = $"startup-barrier-{item.barrier.Sequence:D6}",
                    WorkItemId = work.WorkItemId,
                    WorkSpanId = $"startup-work-{work.Sequence:D6}",
                    CommitSpanId = commitSpan?.SpanId,
                    ModuleKey = work.ModuleKey,
                    Barrier = item.barrier.Barrier,
                    BlockingDurationMs = item.pending.RemainingDurationMs,
                    CommitDurationMs = commitSpan?.DurationMs ?? 0,
                    IsBarrierReleaser = work.IsBarrierReleaser
                };
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<ModuleDiagnosticFinding> CreateFindings(
        ModuleDiagnosticsSource source,
        ModuleDiagnosticsSummary summary,
        ImmutableArray<ModuleDiagnosticsTraceSpan> traceSpans)
    {
        var findings = new List<ModuleDiagnosticFinding>();
        foreach (var budget in summary.PerformanceBudgets.Where(static budget => budget.IsExceeded))
        {
            findings.Add(new ModuleDiagnosticFinding
            {
                Code = ModuleDiagnosticFindingCodes.PERFORMANCE_BUDGET_EXCEEDED,
                Severity = ModuleDiagnosticFindingSeverity.Warning,
                Arguments = ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    [
                        new KeyValuePair<string, string>("metric", budget.Kind.ToString()),
                        new KeyValuePair<string, string>("actualMs", budget.ActualDurationMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("limitMs", budget.LimitDurationMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                    ]),
                Evidence = new ModuleDiagnosticFindingEvidence
                {
                    Kind = ModuleDiagnosticFindingEvidenceKind.PerformanceBudget,
                    PerformanceMetric = budget.Kind,
                    ActualDurationMs = budget.ActualDurationMs,
                    LimitDurationMs = budget.LimitDurationMs
                }
            });
        }

        foreach (var error in source.Errors.Where(static error =>
                     error.ErrorType != ModuleRegistrationErrorType.StartupWorkError))
        {
            var moduleKey = ModuleKey.FromModuleType(error.ModuleType);
            var isCommitFailure = error.ErrorType == ModuleRegistrationErrorType.StartupWorkCommitError;
            findings.Add(new ModuleDiagnosticFinding
            {
                Code = isCommitFailure
                    ? ModuleDiagnosticFindingCodes.STARTUP_WORK_COMMIT_FAILED
                    : ModuleDiagnosticFindingCodes.REGISTRATION_FAILED,
                Severity = ModuleDiagnosticFindingSeverity.Error,
                Arguments = ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    [
                        new KeyValuePair<string, string>("module", error.ModuleType.Name),
                        new KeyValuePair<string, string>("errorKind", error.ErrorType.ToString())
                    ]),
                Evidence = new ModuleDiagnosticFindingEvidence
                {
                    Kind = isCommitFailure
                        ? ModuleDiagnosticFindingEvidenceKind.StartupWorkCommitFailure
                        : ModuleDiagnosticFindingEvidenceKind.RegistrationFailure,
                    ModulePhase = error.Phase,
                    ErrorKind = error.ErrorType.ToString(),
                    WorkItemId = error.WorkItemId
                },
                RelatedModules = [moduleKey],
                RelatedSpanIds = traceSpans
                    .Where(span => error.WorkItemId is not null
                        ? string.Equals(span.WorkItemId, error.WorkItemId, StringComparison.Ordinal)
                        : span.ModuleKey == moduleKey && span.ModulePhase == error.Phase)
                    .Select(static span => span.SpanId)
                    .ToImmutableArray()
            });
        }

        foreach (var work in source.Performance.StartupWorkItems.Where(static work =>
                     work.Status == ModuleStartupWorkStatus.Failed))
        {
            findings.Add(new ModuleDiagnosticFinding
            {
                Code = ModuleDiagnosticFindingCodes.STARTUP_WORK_FAILED,
                Severity = ModuleDiagnosticFindingSeverity.Error,
                Arguments = ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    [
                        new KeyValuePair<string, string>("module", work.ModuleTypeName),
                        new KeyValuePair<string, string>("work", work.Name)
                    ]),
                Evidence = new ModuleDiagnosticFindingEvidence
                {
                    Kind = ModuleDiagnosticFindingEvidenceKind.StartupWorkFailure,
                    ModulePhase = work.OriginPhase,
                    WorkItemId = work.WorkItemId
                },
                RelatedModules = [work.ModuleKey],
                RelatedSpanIds = traceSpans
                    .Where(span => string.Equals(span.WorkItemId, work.WorkItemId, StringComparison.Ordinal))
                    .Select(static span => span.SpanId)
                    .ToImmutableArray()
            });
        }

        if (source.Composition.IsFailed
            && source.Composition.FailureKind is not ModuleCompositionFailureKind.ServiceRegistration)
        {
            var failureKind = source.Composition.FailureKind?.ToString() ?? "Unknown";
            findings.Add(new ModuleDiagnosticFinding
            {
                Code = ModuleDiagnosticFindingCodes.COMPOSITION_FAILED,
                Severity = ModuleDiagnosticFindingSeverity.Error,
                Arguments = ImmutableDictionary.CreateRange(
                    StringComparer.Ordinal,
                    [new KeyValuePair<string, string>("failureKind", failureKind)]),
                Evidence = new ModuleDiagnosticFindingEvidence
                {
                    Kind = ModuleDiagnosticFindingEvidenceKind.CompositionFailure,
                    CompositionFailureKind = failureKind
                },
                RelatedSpanIds = traceSpans
                    .Where(span => source.Composition.FailureKind switch
                    {
                        ModuleCompositionFailureKind.ApplicationPipeline =>
                            span.ModulePhase == ModulePhase.ConfigureApplicationBuilder,
                        ModuleCompositionFailureKind.EndpointMapping =>
                            span.ModulePhase == ModulePhase.ConfigureEndpoints,
                        _ => false
                    })
                    .Select(static span => span.SpanId)
                    .ToImmutableArray()
            });
        }

        return findings.ToImmutableArray();
    }

    private TypeDiscoveryAssemblyInventory CreateAssemblyInventory()
    {
        var analysis = _application.TypeFinder.GetAssemblyAnalysis();
        var wasTypeEnumerationRequired = _application.Profiling.GetTypeDiscoveryStatistics().PlanCount != 0;
        var records = new Dictionary<string, TypeDiscoveryAssemblyRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in analysis.LoadedAssemblies)
        {
            records[assembly.Name] = CreateAssemblyRecord(
                assembly,
                assembly.IsInScanSet
                    ? !wasTypeEnumerationRequired
                        ? TypeDiscoveryAssemblyOutcome.ResolvedNotScanned
                        : assembly.LoadError is null
                            ? TypeDiscoveryAssemblyOutcome.Scanned
                            : TypeDiscoveryAssemblyOutcome.PartialTypeLoad
                    : TypeDiscoveryAssemblyOutcome.Excluded);
        }

        foreach (var assembly in analysis.ScanAssemblies)
        {
            records[assembly.Name] = CreateAssemblyRecord(
                assembly,
                !wasTypeEnumerationRequired
                    ? TypeDiscoveryAssemblyOutcome.ResolvedNotScanned
                    : assembly.LoadError is null
                        ? TypeDiscoveryAssemblyOutcome.Scanned
                        : TypeDiscoveryAssemblyOutcome.PartialTypeLoad);
        }

        foreach (var reference in analysis.ReferencedAssemblies.Where(static reference => reference.LoadError is not null))
        {
            records[reference.Name] = new TypeDiscoveryAssemblyRecord
            {
                Name = reference.Name,
                Version = reference.Version,
                Outcome = TypeDiscoveryAssemblyOutcome.ResolutionFailed,
                Failure = Bound(reference.LoadError)
            };
        }

        foreach (var library in analysis.DependencyLibraries.Where(static library =>
                     library.IsProject && library.LoadError is not null))
        {
            records[library.AssemblyName] = new TypeDiscoveryAssemblyRecord
            {
                Name = library.AssemblyName,
                Version = library.AssemblyVersion,
                Outcome = TypeDiscoveryAssemblyOutcome.ResolutionFailed,
                Failure = Bound(library.LoadError)
            };
        }

        var ordered = records.Values
            .OrderByDescending(static record => record.Outcome == TypeDiscoveryAssemblyOutcome.ResolutionFailed)
            .ThenByDescending(static record => record.Outcome == TypeDiscoveryAssemblyOutcome.PartialTypeLoad)
            .ThenByDescending(static record => record.Outcome == TypeDiscoveryAssemblyOutcome.ResolvedNotScanned)
            .ThenBy(static record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new TypeDiscoveryAssemblyInventory
        {
            CompositionId = _compositionId,
            CapturedAtUtc = analysis.GeneratedAt,
            UsesDefaultProjectAssemblies = analysis.Configuration.UseDefaultProjectAssemblies,
            IncludePatterns = analysis.Configuration.AddPatterns.ToImmutableArray(),
            ExcludePatterns = analysis.Configuration.ExcludePatterns.ToImmutableArray(),
            Assemblies = ordered,
            ScannedCount = ordered.Count(static record => record.Outcome == TypeDiscoveryAssemblyOutcome.Scanned),
            NotScannedCount = ordered.Count(static record =>
                record.Outcome == TypeDiscoveryAssemblyOutcome.ResolvedNotScanned),
            ExcludedCount = ordered.Count(static record => record.Outcome == TypeDiscoveryAssemblyOutcome.Excluded),
            ResolutionFailedCount = ordered.Count(static record =>
                record.Outcome == TypeDiscoveryAssemblyOutcome.ResolutionFailed),
            PartialTypeLoadCount = ordered.Count(static record =>
                record.Outcome == TypeDiscoveryAssemblyOutcome.PartialTypeLoad)
        };
    }

    private static TypeDiscoveryAssemblyRecord CreateAssemblyRecord(
        TypeFinderAssemblyInfo assembly,
        TypeDiscoveryAssemblyOutcome outcome)
    {
        return new TypeDiscoveryAssemblyRecord
        {
            Name = assembly.Name,
            Version = assembly.Version,
            Location = assembly.Location,
            Outcome = outcome,
            IsEntryAssembly = assembly.IsEntryAssembly,
            Failure = Bound(assembly.LoadError)
        };
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length <= MAX_FAILURE_LENGTH ? value : value[..MAX_FAILURE_LENGTH];
    }

    private sealed record ModuleDiagnosticsSource(
        long Revision,
        ModuleCompositionDiagnosticsState Composition,
        ModuleCompositionPerformance Performance,
        TypeDiscoveryStatistics TypeDiscoveryStatistics,
        ImmutableArray<TypeDiscoveryQuerySummary> TypeDiscoveryQueries,
        ImmutableArray<ModuleRegistrationDiagnosticsState> Registrations,
        ImmutableArray<ModuleRuntimeDiagnosticsState> RuntimeModules,
        ImmutableArray<ModuleRegistrationErrorDiagnosticsState> Errors,
        ImmutableDictionary<ModuleKey, ImmutableHashSet<ModuleKey>> Dependencies,
        ImmutableArray<ModuleKey> TopologicalOrder);
}
