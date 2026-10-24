using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.ApplicationModel.Abstractions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.ServiceDiscovery.Abstractions;
using Monica.ServiceDiscovery.Facades;
using Monica.ServiceDiscovery.Localization;
using Monica.ServiceDiscovery.Models;
using Monica.ServiceDiscovery.Providers;
using Monica.ServiceDiscovery.Services;
using Monica.ServiceDiscovery.Services.Support;
using Monica.StateStore.Abstractions;
using Polly;
using Polly.Retry;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public class ModuleServiceDiscovery : MonicaModule<ModuleServiceDiscoveryOption>, IWebModule
{
    internal const string STATE_STORE_FEATURE = "service-discovery-state-store";

    /// <inheritdoc />
    public override void ValidateOptions(ModuleServiceDiscoveryOption options, string? profileName)
    {

        var applicationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in options.PredefinedServices)
        {
            if (string.IsNullOrWhiteSpace(service.AppId) || service.AppId != service.AppId.Trim() || !applicationIds.Add(service.AppId))
                throw new InvalidOperationException("Predefined services require unique, nonempty AppIds without surrounding whitespace.");
        }

        if (!Enum.IsDefined(options.Role))
        {
            throw new InvalidOperationException(
                $"Unsupported {nameof(ServiceDiscoveryRole)} value '{options.Role}'.");
        }

        if (options.StorageMode is not { } storageMode)
        {
            throw new InvalidOperationException(
                $"Service discovery storage is not configured. Call {nameof(ModuleServiceDiscoveryBuilderExtensions.UseMemoryStorage)}, " +
                $"{nameof(ModuleServiceDiscoveryBuilderExtensions.UseDistributedStorage)}, or " +
                $"{nameof(ModuleServiceDiscoveryBuilderExtensions.UseExternalKeyedStorage)} when composing the module.");
        }

        if (!Enum.IsDefined(storageMode))
        {
            throw new InvalidOperationException(
                $"Unsupported {nameof(ServiceDiscoveryStorageMode)} value '{storageMode}'.");
        }

        if (storageMode == ServiceDiscoveryStorageMode.ExternalKeyed)
        {
            if (string.IsNullOrWhiteSpace(options.ExternalStateStoreServiceKey))
            {
                throw new InvalidOperationException(
                    $"{nameof(ModuleServiceDiscoveryOption.ExternalStateStoreServiceKey)} is required when " +
                    $"{nameof(ModuleServiceDiscoveryOption.StorageMode)} is {nameof(ServiceDiscoveryStorageMode.ExternalKeyed)}.");
            }

            return;
        }

        if (options.ExternalStateStoreServiceKey is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(ModuleServiceDiscoveryOption.ExternalStateStoreServiceKey)} can only be configured for " +
                $"{nameof(ServiceDiscoveryStorageMode.ExternalKeyed)} storage.");
        }
    }

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleDomainCatalog, ModuleDomainCatalogOption>();
        module.RequireFeature(STATE_STORE_FEATURE);
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static localization => localization.AddResource<ServiceDiscoveryResource>());
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();
        module.Require<ModuleResilience, ModuleResilienceOption>(resilience =>
            resilience.PipelineConfigurations[ResiliencePipelineNames.ServiceDiscovery] = builder =>
                builder.AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 5,
                    Delay = TimeSpan.FromSeconds(2),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true
                }));
    }

    /// <inheritdoc />
    public override void DeclareContracts(ModuleContractDescriptor<ModuleServiceDiscoveryOption> contracts)
    {
        if (Application.TypeActivation is IApplicationModel model)
            ServiceDiscoveryApplicationIdentity.Validate(contracts.Options, model.Deployment);
        switch (contracts.Options.StorageMode)
        {
            case ServiceDiscoveryStorageMode.Memory:
                contracts.RequireService<IMemoryStateStore>();
                break;
            case ServiceDiscoveryStorageMode.Distributed:
                contracts.RequireService<IDistributedStateStore>();
                break;
            case ServiceDiscoveryStorageMode.ExternalKeyed:
                contracts.RequireKeyedService<IStateStore>(
                    contracts.Options.ExternalStateStoreServiceKey
                    ?? throw new InvalidOperationException("External state-store key validation did not run."));
                break;
            default:
                throw new InvalidOperationException("Service discovery storage validation did not run.");
        }
    }

    public override void ConfigureServices(ModuleContext<ModuleServiceDiscoveryOption> context)
    {
        var services = context.Services;
        switch (Option.StorageMode)
        {
            case ServiceDiscoveryStorageMode.Memory:
                services.AddKeyedSingleton<IStateStore>(nameof(ModuleServiceDiscovery), (provider, _) =>
                    provider.GetRequiredService<IMemoryStateStore>());
                break;
            case ServiceDiscoveryStorageMode.Distributed:
                services.AddKeyedSingleton<IStateStore>(nameof(ModuleServiceDiscovery), (provider, _) =>
                    provider.GetRequiredService<IDistributedStateStore>());
                break;
            case ServiceDiscoveryStorageMode.ExternalKeyed:
                var externalKey = Option.ExternalStateStoreServiceKey
                    ?? throw new InvalidOperationException("External state-store key validation did not run.");
                if (!string.Equals(externalKey, nameof(ModuleServiceDiscovery), StringComparison.Ordinal))
                {
                    services.AddKeyedSingleton<IStateStore>(nameof(ModuleServiceDiscovery), (provider, _) =>
                        provider.GetRequiredKeyedService<IStateStore>(externalKey));
                }
                break;
            default:
                throw new InvalidOperationException("Service discovery storage validation did not run.");
        }

        if (Option.IncludeListeningAddresses)
        {
            // Register IServerAddressesFeature to retrieve listening addresses.
            services.TryAddSingleton(provider =>
            {
                var server = provider.GetService<IServer>();
                return server?.Features.Get<IServerAddressesFeature>()
                       ?? new ServerAddressesFeature();
            });
        }

        // Register the default IServiceDiscoveryClientInfo implementation.
        // TryAddSingleton allows callers to provide a custom implementation when needed.
        services.TryAddSingleton<IServiceDiscoveryClientInfo, DefaultServiceDiscoveryClientInfo>();
        services.AddScoped<ServiceDiscoveryQueryService>();
        services.AddScoped<ServiceDiscoveryFacade>();

        // Register the new StateStore foundational services.
        services.TryAddSingleton<IRegistrationStateManager, RegistrationStateManager>();
        services.TryAddSingleton<ILeaderElectionService, LeaderElectionService>();

        // Register ServiceDiscoveryClientHostedService as a singleton and expose it as both HostedService and Coordinator.
        services.AddSingleton<ServiceDiscoveryClientHostedService>();
        services.AddSingleton<IServiceRegistrationCoordinator>(provider =>
            provider.GetRequiredService<ServiceDiscoveryClientHostedService>());
        services.AddHostedService(provider =>
            provider.GetRequiredService<ServiceDiscoveryClientHostedService>());

        // Register the default information provider implementation.
        services.TryAddSingleton<IServiceDiscoveryCatalogProvider, DefaultServiceDiscoveryCatalogProvider>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleServiceDiscoveryOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet(ServiceDiscoveryConventions.RegistryLeaderStatus,
                async ([FromServices] ServiceDiscoveryFacade facade) =>
                    (await facade.GetRegistryLeaderStatusAsync()).GetResponse())
            .WithName("查询Leader状态")
            .WithTags(tagName)
            .WithSummary("查询 Leader 状态")
            .WithDescription("查询当前实例在服务集群中的 Leader 状态");

            endpoints.MapGet(ServiceDiscoveryConventions.RegistryServiceStatus,
                async ([FromServices] ServiceDiscoveryFacade facade) =>
                    (await facade.GetRegistryServiceStatusAsync()).GetResponse())
            .WithName("获取服务状态")
            .WithTags(tagName)
            .WithSummary("获取服务状态")
            .WithDescription("获取当前实例的注册状态和 Leader 信息");

            endpoints.MapPost("/registry/release-leader",
                async ([FromServices] ServiceDiscoveryFacade facade) =>
                    (await facade.ReleaseLeaderAsync()).GetResponse())
            .WithName("释放Leader状态")
            .WithTags(tagName)
            .WithSummary("释放 Leader 状态")
            .WithDescription("强制当前实例释放 Leader 状态（用于调试/管理）");

            endpoints.MapGet("/registry/election-config",
                ([FromServices] ServiceDiscoveryFacade facade) =>
                    facade.GetElectionConfig().GetResponse())
            .WithName("获取选举配置")
            .WithTags(tagName)
            .WithSummary("获取选举配置")
            .WithDescription("获取当前实例的 Leader 选举配置参数");
        });
    }
}

public static class ModuleServiceDiscoveryBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the ServiceDiscovery module.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> AddServiceDiscovery(
            Action<ModuleServiceDiscoveryOption>? action = null)
        {
            return builder.AddModule<ModuleServiceDiscovery, ModuleServiceDiscoveryOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> registration)
    {
        /// <summary>
        /// Configures this host as a worker. Workers participate in service registration but do not own
        /// application control-plane responsibilities. This is the default role.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> AsWorker()
        {
            return registration.Configure(options => options.Role = ServiceDiscoveryRole.Worker);
        }

        /// <summary>
        /// Configures this host as a registry instance that owns application control-plane responsibilities.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> AsRegistry()
        {
            return registration.Configure(options => options.Role = ServiceDiscoveryRole.Registry);
        }

        /// <summary>
        /// Configures this host as a self-contained registry and worker for single-host deployments.
        /// Storage remains an independent choice and must be selected separately.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> AsStandalone()
        {
            return registration.Configure(options => options.Role = ServiceDiscoveryRole.Standalone);
        }

        /// <summary>
        /// Uses the process-local state store owned by <see cref="ModuleStateStore"/>.
        /// Select this only when service-discovery state does not need to cross process boundaries.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> UseMemoryStorage()
        {
            registration.Require<ModuleStateStore, ModuleStateStoreOption>();
            return registration
                .Configure(options =>
                {
                    options.StorageMode = ServiceDiscoveryStorageMode.Memory;
                    options.ExternalStateStoreServiceKey = null;
                })
                .SatisfyFeature(ModuleServiceDiscovery.STATE_STORE_FEATURE);
        }

        /// <summary>
        /// Uses the distributed provider selected on <see cref="ModuleStateStore"/> and binds it directly to
        /// service discovery without consulting the host's default <see cref="IStateStore"/> registration.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> UseDistributedStorage()
        {
            registration.Require<ModuleStateStore, ModuleStateStoreOption>()
                .RequireFeature(ModuleStateStore.DISTRIBUTED_PROVIDER_FEATURE);
            return registration
                .Configure(options =>
                {
                    options.StorageMode = ServiceDiscoveryStorageMode.Distributed;
                    options.ExternalStateStoreServiceKey = null;
                })
                .SatisfyFeature(ModuleServiceDiscovery.STATE_STORE_FEATURE);
        }

        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> ConfigureRegistryCatalog<TProvider>()
            where TProvider : class, IServiceDiscoveryCatalogProvider
        {
            return registration.ConfigureServices(context =>
                context.Services.AddSingleton<IServiceDiscoveryCatalogProvider, TProvider>());
        }



        /// <summary>
        /// Uses a keyed state store that the host has registered explicitly. The supplied key is the sole external
        /// provider contract; no unkeyed state-store fallback is consulted.
        /// </summary>
        public ModuleRegistration<ModuleServiceDiscovery, ModuleServiceDiscoveryOption> UseExternalKeyedStorage(
            string serviceKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
            return registration
                .Configure(options =>
                {
                    options.StorageMode = ServiceDiscoveryStorageMode.ExternalKeyed;
                    options.ExternalStateStoreServiceKey = serviceKey;
                })
                .SatisfyFeature(ModuleServiceDiscovery.STATE_STORE_FEATURE);
        }
    }
}

public class ModuleServiceDiscoveryOption : MinimalApiModuleOptions<ModuleServiceDiscovery>
{
    /// <summary>
    /// Gets the responsibility assigned to this host. The default is <see cref="ServiceDiscoveryRole.Worker"/>.
    /// Use the role registration methods when the host owns registry or standalone control-plane work.
    /// </summary>
    public ServiceDiscoveryRole Role { get; set; } = ServiceDiscoveryRole.Worker;

    /// <summary>
    /// Gets the state-store binding selected for service discovery. There is no implicit default: composition must
    /// select memory, distributed, or an external keyed provider through the registration API.
    /// </summary>
    public ServiceDiscoveryStorageMode? StorageMode { get; internal set; }
   
    /// <summary>
    /// Environment variable keys to read as metadata.
    /// </summary>
    public List<string> MetadataEnvironmentVariables { get; set; } = new();

    /// <summary>
    /// Indicates whether listening addresses should be included as metadata.
    /// </summary>
    public bool IncludeListeningAddresses { get; set; } = true;
    

    // === Service Identity Configuration ===

    /// <summary>
    /// Subdomain name (optional).
    /// When not configured, ServiceDiscovery uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// </summary>
    public string? DomainName { get; set; }

    /// <summary>
    /// Unique application identifier.
    /// When not configured, ServiceDiscovery uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// With an application model, an explicit value must match its canonical AppId.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Display name of the application.
    /// When not configured, ServiceDiscovery uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// With an application model, the deployment entry owns this value and conflicting overrides fail composition.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// Project name.
    /// When not configured, ServiceDiscovery uses the application defaults configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// </summary>
    public string? ProjectName { get; set; }

    // === Version Information ===

    /// <summary>
    /// Application build time stored in UTC.
    /// Defaults to the entry assembly file last write time in UTC.
    /// Local or unspecified configured values are normalized to UTC.
    /// </summary>
    public DateTime? BuildTime { get; set; }

    /// <summary>
    /// Assembly version (defaults to FileVersionInfo).
    /// </summary>
    public string? AssemblyVersion { get; set; }

    /// <summary>
    /// Release version (custom version identifier).
    /// When not configured, ServiceDiscovery uses the application version configured through <see cref="IMonicaBuilder.ConfigureApplication"/>.
    /// </summary>
    public string? ReleaseVersion { get; set; }

    // === Instance Information ===

    /// <summary>
    /// Instance identifier in the format "hostname:processId".
    /// If not set, it is generated automatically as "{COMPUTERNAME/HOSTNAME/MachineName}:{ProcessId}".
    /// </summary>
    public string? FromInstance { get; set; }

    /// <summary>
    /// Expected applications and optional display names, including applications without online registrations.
    /// Defaults to empty. Every application-model deployment entry is included automatically, including empty-role entries.
    /// Names for known deployment applications must agree with their canonical entries; additional external applications are allowed.
    /// </summary>
    public List<PredefinedServiceInfo> PredefinedServices { get; set; } = [];

    /// <summary>
    /// Leader election configuration.
    /// </summary>
    public ElectionConfig Election { get; set; } = new();

    /// <summary>
    /// Isolation handling mode.
    /// </summary>
    public EIsolationHandlingMode IsolationHandlingMode { get; set; } = EIsolationHandlingMode.ContinueRunning;

    /// <summary>
    /// Gets the host-owned keyed service used when <see cref="StorageMode"/> is
    /// <see cref="ServiceDiscoveryStorageMode.ExternalKeyed"/>. Configure it through
    /// <see cref="ModuleServiceDiscoveryBuilderExtensions.UseExternalKeyedStorage"/>.
    /// </summary>
    public string? ExternalStateStoreServiceKey { get; internal set; }

    #region CoordinatedLeaderService Configuration 
    /// <summary>
    /// Skips waiting for service registration when enabled.
    /// Useful for development and standalone mode. Default: false.
    /// </summary>
    public bool SkipRegistrationWait { get; set; } = false;

    /// <summary>
    /// Maximum time to wait for service registration before starting scheduler services.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan RegistrationWaitTimeout { get; set; } = TimeSpan.FromMinutes(5);
    #endregion
}

/// <summary>
/// Defines the application responsibility owned by a service-discovery host.
/// </summary>
public enum ServiceDiscoveryRole
{
    /// <summary>
    /// Participates in service registration and workload execution without owning application control-plane work.
    /// </summary>
    Worker,

    /// <summary>
    /// Owns application control-plane work while participating in distributed service discovery.
    /// </summary>
    Registry,

    /// <summary>
    /// Combines registry and worker responsibilities in a self-contained host.
    /// </summary>
    Standalone
}

/// <summary>
/// Defines how service-discovery state is bound to a concrete store.
/// </summary>
public enum ServiceDiscoveryStorageMode
{
    /// <summary>
    /// Uses the process-local <see cref="IMemoryStateStore"/>.
    /// </summary>
    Memory,

    /// <summary>
    /// Uses the <see cref="IDistributedStateStore"/> selected on the StateStore module.
    /// </summary>
    Distributed,

    /// <summary>
    /// Uses a host-owned keyed <see cref="IStateStore"/> registration.
    /// </summary>
    ExternalKeyed
}

/// <summary>
/// Isolation handling mode.
/// </summary>
public enum EIsolationHandlingMode
{
    /// <summary>
    /// Continue running in degraded mode without participating in leader election.
    /// </summary>
    ContinueRunning,

    /// <summary>
    /// Shut down quickly.
    /// </summary>
    FastShutdown
}
