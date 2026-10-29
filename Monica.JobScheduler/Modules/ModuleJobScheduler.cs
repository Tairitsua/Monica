using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.HealthCheck.Extensions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Annotations;
using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Facades;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.JobScheduler.Services;
using Monica.JobScheduler.Services.Support;
using Monica.Locker.Leases;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleJobSchedulerBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Adds the JobScheduler runtime for the host's selected logical owners. Automatic hosts require a store;
        /// Manual hosts use a fresh isolated memory store and execute only explicit work. The scheduler scope comes
        /// from Deployment:Scope unless UseSchedulerScope overrides it. Application activation selects
        /// role-owned jobs before they enter the local catalog.
        /// </summary>
        public ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> AddJobScheduler(
            Action<ModuleJobSchedulerOption>? action = null)
        {
            return builder.AddModule<ModuleJobScheduler, ModuleJobSchedulerOption>(action);
        }
    }
}

/// <summary>
/// Composes owner-local scheduling and execution. Manual hosts initialize a local catalog and retain the native
/// executor while automatic recurring scheduling and shared maintenance remain held.
/// </summary>
public sealed class ModuleJobScheduler : MonicaModule<ModuleJobSchedulerOption>
{
    private readonly List<LocalJobDefinition> _localJobDefinitions = [];

    /// <inheritdoc />
    public override void ValidateOptions(ModuleJobSchedulerOption options, string? profileName)
    {
        options.AutonomousWork = Application.Application.AutonomousWork;
        if (string.IsNullOrWhiteSpace(options.SchedulerScopeKey))
            options.SchedulerScopeKey = Application.Application.DeploymentScope ?? string.Empty;
        ValidateRuntimeOptions(options);
    }

    internal static void ValidateRuntimeOptions(ModuleJobSchedulerOption options)
    {
        ValidatePositive(options.SchedulingPollInterval, nameof(options.SchedulingPollInterval));
        ValidatePositive(options.WorkerPollInterval, nameof(options.WorkerPollInterval));
        ValidatePositive(options.SnapshotSyncInterval, nameof(options.SnapshotSyncInterval));
        ValidatePositive(options.CoordinatorLeaseDuration, nameof(options.CoordinatorLeaseDuration));
        ValidatePositive(options.StoreOperationTimeout, nameof(options.StoreOperationTimeout));
        if (options.MaxConcurrentStoreOperations < 2 || options.ReservedLeaseStoreOperations < 1
            || options.ReservedLeaseStoreOperations >= options.MaxConcurrentStoreOperations)
            throw new InvalidOperationException("Store admission must reserve at least one slot for leases and one for other operations.");
        ValidatePositive(options.ExecutionLeaseDuration, nameof(options.ExecutionLeaseDuration));
        ValidatePositive(options.ExecutionLeaseRenewInterval, nameof(options.ExecutionLeaseRenewInterval));
        ValidatePositive(options.ExecutionLeaseSafetyMargin, nameof(options.ExecutionLeaseSafetyMargin));
        ValidatePositive(options.ExecutionLeaseRenewalTimeout, nameof(options.ExecutionLeaseRenewalTimeout));
        ValidatePositive(options.ExecutionFinalizationTimeout, nameof(options.ExecutionFinalizationTimeout));
        ValidatePositive(options.ExecutionRetryDelay, nameof(options.ExecutionRetryDelay));
        ValidatePositive(options.ExecutionCancellationGracePeriod, nameof(options.ExecutionCancellationGracePeriod));
        ValidatePositive(options.WorkerShutdownGracePeriod, nameof(options.WorkerShutdownGracePeriod));
        ValidatePositive(options.HistoryCleanupInterval, nameof(options.HistoryCleanupInterval));

        if (options.ExecutionLeaseRenewInterval >= options.ExecutionLeaseDuration)
        {
            throw new InvalidOperationException(
                $"{nameof(options.ExecutionLeaseRenewInterval)} must be shorter than " +
                $"{nameof(options.ExecutionLeaseDuration)}.");
        }

        if (options.ExecutionLeaseRenewInterval + options.ExecutionCancellationGracePeriod
            + options.ExecutionLeaseSafetyMargin >= options.ExecutionLeaseDuration)
        {
            throw new InvalidOperationException(
                $"{nameof(options.ExecutionLeaseDuration)} must exceed {nameof(options.ExecutionLeaseRenewInterval)} + " +
                $"{nameof(options.ExecutionCancellationGracePeriod)} + {nameof(options.ExecutionLeaseSafetyMargin)}.");
        }

        // Grace-period waits stop lease renewal, so a grace period at or beyond the lease duration could let another
        // host recover and requeue an execution while the original attempt is still draining it.
        if (options.ExecutionCancellationGracePeriod >= options.ExecutionLeaseDuration)
        {
            throw new InvalidOperationException(
                $"{nameof(options.ExecutionCancellationGracePeriod)} must be shorter than " +
                $"{nameof(options.ExecutionLeaseDuration)}.");
        }

        if (options.WorkerShutdownGracePeriod >= options.ExecutionLeaseDuration)
        {
            throw new InvalidOperationException(
                $"{nameof(options.WorkerShutdownGracePeriod)} must be shorter than " +
                $"{nameof(options.ExecutionLeaseDuration)}.");
        }

        if (options.MaxWorkerExecutionThreads < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxWorkerExecutionThreads)} must be greater than zero.");
        }

        if (options.MaxClaimBatchSize is < 1 or > JobClaimRequest.MAX_COUNT)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxClaimBatchSize)} must be between 1 and {JobClaimRequest.MAX_COUNT}.");
        }

        if (options.MaxRecurringMaterializationsPerCycle < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxRecurringMaterializationsPerCycle)} must be greater than zero.");
        }

        if (options.MaxExpiredLeaseRecoveriesPerCycle < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxExpiredLeaseRecoveriesPerCycle)} must be greater than zero.");
        }

        if (options.MaxLeaseLossesBeforeFailure < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxLeaseLossesBeforeFailure)} must be greater than zero.");
        }

        if (options.MaxExecutionHistoryEntriesPerExecution < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxExecutionHistoryEntriesPerExecution)} must be greater than zero.");
        }

        if (options.MaxExecutionHistoryMessageLength < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxExecutionHistoryMessageLength)} must be greater than zero.");
        }

        if (options.MaxHistoryDeletionsPerCycle < 1)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxHistoryDeletionsPerCycle)} must be greater than zero.");
        }

        if (options.MaxRetainedOrphanedExecutions < 0)
        {
            throw new InvalidOperationException(
                $"{nameof(options.MaxRetainedOrphanedExecutions)} cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(options.SchedulerScopeKey))
        {
            throw new InvalidOperationException("A scheduler scope requires Deployment:Scope or UseSchedulerScope(...).");
        }

        if (string.IsNullOrWhiteSpace(options.WorkerInstanceId))
        {
            throw new InvalidOperationException($"{nameof(options.WorkerInstanceId)} cannot be empty.");
        }

        JobSchedulerIdentity.ValidateStandard(options.SchedulerScopeKey, nameof(options.SchedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(options.WorkerInstanceId, nameof(options.WorkerInstanceId));
        JobSchedulerIdentity.ValidateStandard(options.GetProjectName(), "ProjectName");
    }

    /// <summary>
    /// Discovers executable job types once and captures their immutable code declarations.
    /// </summary>
    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleJobSchedulerOption> discovery)
    {
        var recurringJobs = TypeQuery.ConcreteClass.AssignableTo<IRecurringJob>();
        var triggeredJobs = TypeQuery.ConcreteClass.ImplementsOpenGeneric(typeof(ITriggeredJob<>));

        discovery.Match(
            TypeQuery.AnyOf(recurringJobs, triggeredJobs),
            (context, matches) =>
            {
                foreach (var match in matches)
                {
                    var mode = Application.TypeActivation!.GetActivation(match.Type, TypeActivationKind.Job);
                    Application.RecordActivation(new ResourceActivation
                    {
                        Key = $"job:{match.Type.FullName}", ImplementationType = match.Type,
                        Kind = TypeActivationKind.Job, Mode = mode,
                        Reason = mode switch
                        {
                            ActivationMode.Manual => "Explicit jobs execute in this process's isolated memory store.",
                            ActivationMode.Disabled => "This job's owner is not placed in this application.",
                            _ => "Normal scheduling and execution are admitted."
                        }
                    });
                    if (mode == ActivationMode.Disabled)
                    {
                        continue;
                    }

                    var isRecurring = match.Shape.IsAssignableTo(typeof(IRecurringJob));
                    var argumentsType = isRecurring
                        ? null
                        : match.OpenGenericInterfaces.Single().GenericArguments[0];
                    var localDefinition = CreateLocalDefinition(
                        match,
                        isRecurring ? JobType.Recurring : JobType.Triggered,
                        argumentsType);

                    _localJobDefinitions.Add(localDefinition);
                    context.Registrations.Add(ServiceDescriptor.Transient(
                        localDefinition.JobClrType,
                        localDefinition.JobClrType));
                }
            });
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleJobSchedulerOption> context)
    {
        foreach (var owner in Application.TypeActivation?.ActiveWorkloadOwners ?? [])
            JobSchedulerIdentity.ValidateStandard(owner, "WorkloadOwner");
        var services = context.Services;
        IReadOnlyList<LocalJobDefinition> localDefinitions =
            Array.AsReadOnly(_localJobDefinitions.OrderBy(
                static definition => definition.Declaration.JobKey,
                StringComparer.Ordinal).ToArray());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JobSchedulerRuntimeState>();
        services.AddSingleton(localDefinitions);

        services.AddSingleton<JobRegistry>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<JobOrchestrator>();
        services.AddSingleton<ITriggeredJobManager, TriggeredJobManager>();
        services.AddSingleton<JobSchedulerFacade>();
        if (Option.AutonomousWork && Option.HistoryCleanupLeaseStore is { } cleanupSelection)
            services.AddSingleton(provider => new HistoryCleanupLeaseStore(
                provider.GetRequiredKeyedService<IExclusiveLeaseStore>(cleanupSelection.ServiceKey)));
        services.AddSingleton<JobSchedulingHostedService>();
        services.AddSingleton<JobExecutionWorkerHostedService>();
        services.AddHostedService(static provider => provider.GetRequiredService<JobSchedulingHostedService>());
        context.Composition.AdmitHostedLifecycle(services.Last(descriptor => descriptor.ServiceType == typeof(IHostedService)), typeof(JobSchedulingHostedService));
        services.AddHostedService(static provider => provider.GetRequiredService<JobExecutionWorkerHostedService>());
        context.Composition.AdmitHostedLifecycle(services.Last(descriptor => descriptor.ServiceType == typeof(IHostedService)), typeof(JobExecutionWorkerHostedService));

        if (!Option.AutonomousWork)
        {
            ServiceDescriptor? localStore = null;
            context.Composition.OnServicesFinalized(finalServices =>
            {
                finalServices.RemoveAll<IJobSchedulerStore>();
                localStore = ServiceDescriptor.Singleton<IJobSchedulerStore>(provider =>
                    new InMemoryJobSchedulerStore(provider.GetRequiredService<TimeProvider>(),
                        provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModuleJobSchedulerOption>>()));
                finalServices.Add(localStore);
            }, descriptors =>
            {
                if (descriptors.Where(descriptor => descriptor.ServiceType == typeof(IJobSchedulerStore)).Any(descriptor => !ReferenceEquals(descriptor, localStore)))
                    throw new InvalidOperationException("Manual JobScheduler requires its isolated process-local store.");
            });
        }

        services.AddHealthChecks().AddMonicaReadinessCheck<JobSchedulerHealthCheck>(
            "monica.job-scheduler",
            tags: ["scheduler"]);
    }

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        module.Require<ModuleHealthCheck, ModuleHealthCheckOption>();
    }

    /// <inheritdoc />
    public override void DeclareContracts(ModuleContractDescriptor<ModuleJobSchedulerOption> contracts)
    {
        contracts.RequireService<IJobSchedulerStore>();
        if (contracts.Options.AutonomousWork && contracts.Options.HistoryCleanupLeaseStore is { } cleanupSelection)
            contracts.RequireKeyedService<IExclusiveLeaseStore>(cleanupSelection.ServiceKey);
    }

    private LocalJobDefinition CreateLocalDefinition(
        BusinessTypeMatch match,
        JobType jobType,
        Type? argumentsType)
    {
        var jobClrType = match.Type;
        var attribute = match.Shape.GetAttribute<JobConfigAttribute>();
        var jobKey = jobClrType.FullName
                     ?? throw new JobRegistrationException(
                         $"Job type '{jobClrType.Name}' must have a full name.",
                         jobClrType.Name);
        var argumentsKey = argumentsType?.FullName;
        if (argumentsType is not null && argumentsKey is null)
        {
            throw new JobRegistrationException(
                $"Argument type '{argumentsType.Name}' for job '{jobKey}' must have a full name.",
                jobKey);
        }

        var declaration = new JobDeclaration
        {
            JobKey = jobKey,
            JobArgsKey = argumentsKey,
            JobName = attribute?.JobName ?? jobClrType.Name,
            Description = attribute?.Description,
            JobType = jobType,
            MaxConcurrency = attribute?.MaxConcurrencyBridge ?? 1,
            RetryCount = attribute?.RetryCountBridge ?? 0,
            MaxExecutionTimeout = attribute?.MaxExecutionTimeout ?? TimeSpan.FromHours(1),
            IsDisabledByDefault = attribute?.IsDisabledBridge ?? false,
            CronExpression = jobType == JobType.Recurring ? attribute?.CronSchedule : null,
            TimeZoneId = jobType == JobType.Recurring ? Option.CronTimeZone.Id : null,
            StartTimeUtc = jobType == JobType.Recurring ? NormalizeBoundary(attribute?.StartTimeBridge) : null,
            EndTimeUtc = jobType == JobType.Recurring ? NormalizeBoundary(attribute?.EndTimeBridge) : null
        };

        declaration = NormalizeDeclaration(declaration);
        var ownerKey = Application.TypeActivation?.GetWorkloadOwner(jobClrType) ?? Option.GetProjectName();
        JobSchedulerIdentity.ValidateStandard(ownerKey, nameof(LocalJobDefinition.OwnerKey));
        return new LocalJobDefinition
        {
            OwnerKey = ownerKey,
            Declaration = declaration,
            JobClrType = jobClrType,
            JobArgsClrType = argumentsType
        };
    }

    private static JobDeclaration NormalizeDeclaration(JobDeclaration declaration)
    {
        try
        {
            return declaration.NormalizeAndValidate();
        }
        catch (Exception exception)
        {
            throw new JobRegistrationException(
                $"Job '{declaration.JobKey}' has an invalid declaration: {exception.Message}",
                declaration.JobKey);
        }
    }

    private DateTimeOffset? NormalizeBoundary(DateTime? boundary)
    {
        if (boundary is not { } value)
        {
            return null;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
            _ => new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(value, Option.CronTimeZone),
                TimeSpan.Zero)
        };
    }

    private static void ValidatePositive(TimeSpan value, string propertyName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{propertyName} must be greater than zero.");
        }
    }
}

/// <summary>
/// Adds persistence configuration to a JobScheduler module registration.
/// </summary>
public static class ModuleJobSchedulerRegistrationExtensions
{
    /// <summary>
    /// Registers a custom unified scheduler store. The implementation must make definition, queue, lease, and
    /// recurring-cursor mutations durable and atomic according to <see cref="IJobSchedulerStore"/>.
    /// </summary>
    public static ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> UseStore<TStore>(
        this ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> registration)
        where TStore : class, IJobSchedulerStore
    {
        return registration.PostConfigureServices(static context =>
            context.Services.TryAddSingleton<IJobSchedulerStore, TStore>());
    }

    /// <summary>
    /// Uses a process-local scheduler store. Select this provider for standalone development and deterministic tests;
    /// it does not survive process restarts and cannot coordinate multiple hosts.
    /// </summary>
    public static ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> UseInMemoryStore(
        this ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> registration)
    {
        return registration.UseStore<InMemoryJobSchedulerStore>();
    }

    /// <summary>
    /// Configures the stable scheduler namespace shared by every host in one environment. Hosts discover, schedule,
    /// and execute their own jobs inside this scope; different environments or logical scheduler installations must
    /// use different scope keys.
    /// </summary>
    public static ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> UseSchedulerScope(
        this ModuleRegistration<ModuleJobScheduler, ModuleJobSchedulerOption> registration,
        string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        return registration.Configure(options => options.SchedulerScopeKey = scopeKey);
    }
}

/// <summary>
/// Configures the owner-local scheduling, leased queue, and maintenance behavior of JobScheduler.
/// </summary>
public sealed class ModuleJobSchedulerOption : ModuleOptions<ModuleJobScheduler>
{
    /// <summary>
    /// Gets or sets the timezone used to evaluate recurring cron expressions. All persisted occurrences are converted
    /// to UTC. The default is the operating system's local timezone.
    /// </summary>
    public TimeZoneInfo CronTimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>
    /// Gets the stable scheduler namespace configured through <see cref="ModuleJobSchedulerRegistrationExtensions.UseSchedulerScope"/>.
    /// </summary>
    public string SchedulerScopeKey { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fallback owner identity for jobs without application-model workload ownership.
    /// Role-owned jobs use the logical owner selected by the host's activation policy instead.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Gets or sets the concrete process identity used to fence execution leases. The default uses the
    /// Kubernetes hostname when present, otherwise the machine name and process identifier.
    /// </summary>
    public string WorkerInstanceId { get; set; } =
        Environment.GetEnvironmentVariable("HOSTNAME") ?? $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <summary>
    /// Gets the frozen host's permission to initiate scheduling and maintenance. Derived from Application:AutonomousWork;
    /// false selects an isolated local store and keeps explicit native execution available.
    /// </summary>
    public bool AutonomousWork { get; internal set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of attempts this process executes concurrently. The default is 4.
    /// </summary>
    public int MaxWorkerExecutionThreads { get; set; } = 4;

    /// <summary>
    /// Gets or sets the maximum durable rows claimed in one worker poll. The effective value never exceeds the
    /// currently available worker slots and must be between 1 and 256. The default is 16.
    /// </summary>
    public int MaxClaimBatchSize { get; set; } = 16;

    /// <summary>
    /// Gets or sets how often each host checks due recurring cursors, expired leases, and history cleanup. Store-level
    /// compare-and-swap operations coordinate replicas. The default is one second.
    /// </summary>
    public TimeSpan SchedulingPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how often a host polls the durable queue when it has execution capacity. The default is 250 ms.
    /// </summary>
    public TimeSpan WorkerPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets or sets how often a host republishes its complete local definition snapshot after startup. The default is
    /// 30 seconds.
    /// </summary>
    public TimeSpan SnapshotSyncInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the grant duration for owner scheduling and scope maintenance coordinators. The default is
    /// 30 seconds. Coordinated mutations recheck the grant under its row lock before touching job state.
    /// </summary>
    public TimeSpan CoordinatorLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional keyed authority for duplicate-tolerant terminal-history pruning. Null shares the scheduler store's
    /// maintenance lease. An external store only coordinates cleanup; owner scheduling and execution recovery
    /// always retain the scheduler database's transaction guards. Scope must identify the protected database.
    /// All replicas must use the same selection. Ignored when autonomous work is disabled.
    /// </summary>
    public LeaseStoreSelection? HistoryCleanupLeaseStore { get; set; }

    /// <summary>
    /// Gets explicit previous-manifest hashes authorized for replacement, indexed by logical owner. Configure this
    /// only during a drained, all-writers-stopped deployment. Empty by default; incompatible manifests fail closed.
    /// </summary>
    public Dictionary<string, string> OwnerManifestReplacements { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the total per-process relational store concurrency limit. The default is eight.</summary>
    public int MaxConcurrentStoreOperations { get; set; } = 8;

    /// <summary>Gets or sets slots reserved from ordinary reads, admission and maintenance for renewal/completion. The default is two.</summary>
    public int ReservedLeaseStoreOperations { get; set; } = 2;

    /// <summary>
    /// Gets or sets the total local admission, transaction and retry budget for a relational store operation.
    /// Completion and release use <see cref="ExecutionFinalizationTimeout"/> instead. The default is five seconds.
    /// </summary>
    public TimeSpan StoreOperationTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the duration of each fenced execution lease. It must exceed
    /// <see cref="ExecutionLeaseRenewInterval"/>, <see cref="ExecutionCancellationGracePeriod"/>, and
    /// <see cref="WorkerShutdownGracePeriod"/> so a grace-period wait never outlives the lease that fences the
    /// attempt. The default is 30 seconds.
    /// </summary>
    public TimeSpan ExecutionLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how often a running attempt renews its execution lease and observes durable cancellation. The
    /// default is 10 seconds.
    /// </summary>
    public TimeSpan ExecutionLeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the time reserved before a conservatively estimated lease expiration. The watchdog starts
    /// cancellation one cancellation grace period before this boundary. The default is two seconds.
    /// </summary>
    public TimeSpan ExecutionLeaseSafetyMargin { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the independent deadline of a renewal request. An unresponsive request never blocks the lease
    /// watchdog and no second renewal is started until the first request exits. The default is five seconds.
    /// </summary>
    public TimeSpan ExecutionLeaseRenewalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum wait for recording completion or releasing a stopped attempt, including relational
    /// store admission, commands and retries. This budget does not extend the execution lease. Expired finalization
    /// is observed in the background and durable recovery remains authoritative. The default is 30 seconds.
    /// </summary>
    public TimeSpan ExecutionFinalizationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the delay applied by the durable store before a failed attempt becomes retryable. The default is
    /// five seconds.
    /// </summary>
    public TimeSpan ExecutionRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets how long a worker keeps its lease while waiting for job code to observe a timeout, durable
    /// cancellation, lease loss, or execution-loop failure. If the job still has not exited, the worker requests host
    /// shutdown so an external supervisor can terminate the unsafe process before the lease is recovered elsewhere.
    /// Must be shorter than <see cref="ExecutionLeaseDuration"/>. The default is 15 seconds.
    /// </summary>
    public TimeSpan ExecutionCancellationGracePeriod { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the maximum total history entries retained for one execution, including state transitions,
    /// job-code logs, and cancellation audit entries. Each append atomically evicts the oldest entries beyond this
    /// boundary while authoritative execution state remains on the execution record. The default is 1,000.
    /// </summary>
    public int MaxExecutionHistoryEntriesPerExecution { get; set; } =
        JobExecutionHistoryLimits.DEFAULT_MAX_ENTRIES;

    /// <summary>
    /// Gets or sets the maximum number of characters persisted for every execution-history message, including job
    /// logs, failure details, state transitions, and cancellation reasons. Longer messages are truncated with an
    /// ellipsis. The default is 4,096 characters.
    /// </summary>
    public int MaxExecutionHistoryMessageLength { get; set; } =
        JobExecutionHistoryLimits.DEFAULT_MAX_MESSAGE_LENGTH;

    /// <summary>
    /// Gets or sets how long the worker waits for cooperative job cancellation during graceful shutdown. The worker
    /// stops renewing execution leases when shutdown begins; attempts that do not exit before this boundary remain
    /// fenced until their lease expires and are then recovered by a host in the scope. Must be shorter than
    /// <see cref="ExecutionLeaseDuration"/>. The default is 15 seconds.
    /// </summary>
    public TimeSpan WorkerShutdownGracePeriod { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the global occurrence budget shared by this owner's recurring cursors in one scheduling cycle.
    /// Overdue cursors are revisited in durable due-time order until this budget is exhausted or the backlog is caught
    /// up. The default is 100.
    /// </summary>
    public int MaxRecurringMaterializationsPerCycle { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum expired execution leases recovered per scheduling cycle. The default is 100.
    /// </summary>
    public int MaxExpiredLeaseRecoveriesPerCycle { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of expired-lease recoveries one execution may accumulate before the
    /// scheduling plane records a failed attempt instead of requeueing it again. Recoveries also fail an
    /// execution immediately when its expired attempt already exceeded the declaration's
    /// <c>MaxExecutionTimeout</c>, regardless of this budget. Raise the value only for workers whose leases are
    /// expected to expire benignly (for example frequent rolling restarts); lower it to stop repeated re-execution
    /// of work that cannot hold a lease. The default is 5.
    /// </summary>
    public int MaxLeaseLossesBeforeFailure { get; set; } = 5;

    /// <summary>
    /// Gets or sets whether terminal execution history is periodically trimmed according to active job policies. The
    /// default is <see langword="true"/>.
    /// </summary>
    public bool EnableHistoryCleanup { get; set; } = true;

    /// <summary>
    /// Gets or sets the interval after a partial durable history cleanup pass. Full batches continue on the next
    /// scheduling poll until the eligible backlog is drained. The default is one hour.
    /// </summary>
    public TimeSpan HistoryCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets the maximum terminal executions deleted in one cleanup pass. The default is 5,000.
    /// </summary>
    public int MaxHistoryDeletionsPerCycle { get; set; } = 5000;

    /// <summary>
    /// Gets or sets the maximum newest terminal executions retained for jobs absent from every definition policy. Zero
    /// disables count-based orphan cleanup. The default is 10.
    /// </summary>
    public int MaxRetainedOrphanedExecutions { get; set; } = 10;

    /// <summary>
    /// Gets or sets the serializer used for triggered-job argument payloads. The default preserves non-ASCII text.
    /// </summary>
    public JsonSerializerOptions JobArgsSerializerOptions { get; set; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Resolves the fallback job-owner identity from explicit scheduler configuration, Monica application configuration,
    /// or the process entry assembly. Workload-specific owners take precedence for discovered jobs.
    /// An explicitly configured project name resolves without host binding.
    /// </summary>
    public string GetProjectName()
    {
        return !string.IsNullOrWhiteSpace(ProjectName)
            ? ProjectName
            : Application.ResolveProjectName(fallback: Assembly.GetEntryAssembly()?.GetName().Name);
    }
}
