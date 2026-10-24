using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.Core.Extensions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Services;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Framework.UI.UIEventBus.State;
using Monica.Modules;

namespace Monica.Framework.UI.UIEventBus.Support;

/// <summary>
/// EventBus Provider Discovery Service - Provides Provider discovery and information query
/// </summary>
public class EventBusProviderDiscoveryService(
    IServiceProvider serviceProvider,
    MonicaApplication application,
    IEventSubscriptionRegistry subscriptionManager,
    ITopicSubscriptionStatusStore topicStatusStore,
    IStringLocalizer<EventBusResource> localizer,
    ILogger<EventBusProviderDiscoveryService> logger)
{
    /// <summary>
    /// Cached provider snapshots for efficient lookup
    /// </summary>
    private IReadOnlyList<ModuleRuntimeSnapshot>? _providerSnapshots;
    private ModuleRuntimeSnapshot? _eventBusSnapshot;

    /// <summary>
    /// Gets or initializes the cached provider snapshots
    /// </summary>
    private IReadOnlyList<ModuleRuntimeSnapshot> ProviderSnapshots =>
        _providerSnapshots ??= application.Modules.GetModuleProviders(typeof(ModuleEventBus));

    private ModuleRuntimeSnapshot? EventBusSnapshot =>
        _eventBusSnapshot ??= application.Modules.RuntimeSnapshots.FirstOrDefault(snapshot =>
            snapshot.ModuleType == typeof(ModuleEventBus));

    #region Provider Discovery

    /// <summary>
    /// Get all registered EventBus Providers
    /// </summary>
    public Res<List<EventBusProviderInfo>> GetRegisteredProviders()
    {
        try
        {
            var providers = new List<EventBusProviderInfo>();

            // Inspect registrations without constructing gateways, transports or external clients.
            var inspector = serviceProvider.GetRequiredService<IServiceProviderIsService>();
            var keyedInspector = serviceProvider.GetService<IServiceProviderIsKeyedService>();
            if (inspector.IsService(typeof(ILocalEventBus)))
                providers.Add(CreateLocalProviderInfo(null));
            if (inspector.IsService(typeof(IDistributedEventBus)) && inspector.IsService(typeof(IEventTransport)))
                providers.Add(CreateDistributedProviderInfo(null));

            var keys = application.Modules.GetKeyedServiceKeys(typeof(ModuleEventBus))
                .Concat(ProviderSnapshots.SelectMany(snapshot => application.Modules.GetKeyedServiceKeys(snapshot.ModuleType)))
                .Concat(subscriptionManager.GetAll().Select(subscription => subscription.ServiceKey).OfType<string>())
                .Distinct(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (keyedInspector?.IsKeyedService(typeof(ILocalEventBus), key) == true)
                    providers.Add(CreateLocalProviderInfo(key));
                if (keyedInspector?.IsKeyedService(typeof(IDistributedEventBus), key) == true &&
                    keyedInspector.IsKeyedService(typeof(IEventTransport), key))
                    providers.Add(CreateDistributedProviderInfo(key));
            }

            // 6. Populate subscription statistics
            PopulateSubscriptionCounts(providers);

            return Res.Ok(providers);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取已注册 EventBus Provider 失败");
            return Res.Fail(localizer["Services:Providers:GetRegisteredFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Get Local Provider based on service key
    /// </summary>
    public Res<ILocalEventBus> GetLocalProvider(string? serviceKey)
    {
        try
        {
            ILocalEventBus? provider = serviceKey == null
                ? serviceProvider.GetService<ILocalEventBus>()
                : serviceProvider.GetKeyedService<ILocalEventBus>(serviceKey);

            if (provider == null)
            {
                return Res.Fail(localizer[
                    "Services:Providers:LocalNotFound",
                    serviceKey ?? localizer["Services:Common:DefaultProviderKey"].Value]);
            }

            return Res.Ok(provider);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取 Local Provider 失败: {Key}", serviceKey);
            return Res.Fail(localizer["Services:Providers:GetProviderFailed", ex.GetMessageRecursively()]);
        }
    }

    /// <summary>
    /// Get Distributed Provider based on service key
    /// </summary>
    public Res<IDistributedEventBus> GetDistributedProvider(string? serviceKey)
    {
        try
        {
            IDistributedEventBus? provider = serviceKey == null
                ? serviceProvider.GetService<IDistributedEventBus>()
                : serviceProvider.GetKeyedService<IDistributedEventBus>(serviceKey);

            if (provider == null || (serviceKey == null
                    ? serviceProvider.GetService<IEventTransport>()
                    : serviceProvider.GetKeyedService<IEventTransport>(serviceKey)) is null)
            {
                return Res.Fail(localizer[
                    "Services:Providers:DistributedNotFound",
                    serviceKey ?? localizer["Services:Common:DefaultProviderKey"].Value]);
            }

            return Res.Ok(provider);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取 Distributed Provider 失败: {Key}", serviceKey);
            return Res.Fail(localizer["Services:Providers:GetProviderFailed", ex.GetMessageRecursively()]);
        }
    }

    #endregion

    #region Private Methods

    private EventBusProviderInfo CreateLocalProviderInfo(string? serviceKey)
    {
        return new EventBusProviderInfo
        {
            ServiceKey = serviceKey,
            ProviderType = EventBusProviderKind.Local,
            Capabilities = EventBusProviderCapabilities.None,
            IsDistributed = false,
            ImplementationType = EventBusSnapshot?.ModuleType.Name ?? localizer["Shared:Labels:Unknown"].Value,
            OptionDiagnosticsTarget = EventBusSnapshot is { } snapshot
                ? new ModuleOptionDiagnosticsTarget(
                    snapshot.ModuleKey,
                    ModuleOptionProfileSelector.Default)
                : null
        };
    }

    private EventBusProviderInfo CreateDistributedProviderInfo(string? serviceKey)
    {
        var (providerType, capabilities, providerSnapshot) = GetDistributedProviderMetadata(serviceKey);

        return new EventBusProviderInfo
        {
            ServiceKey = serviceKey,
            ProviderType = providerType,
            Capabilities = capabilities,
            IsDistributed = true,
            ImplementationType = providerSnapshot?.ModuleType.Name ?? localizer["Shared:Labels:Unknown"].Value,
            OptionDiagnosticsTarget = providerSnapshot is null
                ? null
                : new ModuleOptionDiagnosticsTarget(
                    providerSnapshot.ModuleKey,
                    serviceKey is null
                        ? ModuleOptionProfileSelector.Default
                        : ModuleOptionProfileSelector.NamedOrDefault(serviceKey))
        };
    }

    /// <summary>
    /// Gets provider metadata from the registered IEventBusProviderModule
    /// </summary>
    private (
        EventBusProviderKind ProviderType,
        EventBusProviderCapabilities Capabilities,
        ModuleRuntimeSnapshot? ProviderSnapshot) GetDistributedProviderMetadata(
        string? serviceKey)
    {
        // A named module registration identifies its provider family. Multiple candidates remain unknown;
        // opening diagnostics must not instantiate clients to guess the winning transport implementation.
        var candidates = ProviderSnapshots.Where(snapshot => snapshot.ModuleInstance is IEventBusProviderModule &&
            (serviceKey is null || application.Modules.GetKeyedServiceKeys(snapshot.ModuleType).Contains(serviceKey)))
            .ToArray();
        if (candidates.Length == 1 && candidates[0].ModuleInstance is IEventBusProviderModule provider)
            return (provider.ProviderType, provider.Capabilities, candidates[0]);
        return (EventBusProviderKind.Unknown, EventBusProviderCapabilities.None, null);
    }

    /// <summary>
    /// Populate subscription statistics for the Provider list
    /// </summary>
    private void PopulateSubscriptionCounts(List<EventBusProviderInfo> providers)
    {
        var allSubscriptions = subscriptionManager.GetAll()
            .Where(subscription => !EventBusTestMetadataKeys.IsTestListenerSubscription(subscription))
            .ToList();

        foreach (var provider in providers)
        {
            var matchingSubscriptions = allSubscriptions.Where(s =>
            {
                // Match ServiceKey
                var keyMatches = s.ServiceKey == provider.ServiceKey;

                // Match Scope
                var scopeMatches = provider.IsDistributed
                    ? s.Scope == EventSubscriptionScope.Distributed
                    : s.Scope == EventSubscriptionScope.Local;

                return keyMatches && scopeMatches;
            }).ToList();

            provider.SubscriptionCount = matchingSubscriptions.Count;
            provider.ActiveSubscriptionCount = matchingSubscriptions.Count(s => s.State == EventSubscriptionState.Active);
            provider.UnhealthyTopicCount = provider.IsDistributed
                ? topicStatusStore.GetAll().Count(t =>
                    t.ServiceKey == provider.ServiceKey &&
                    t.State is TopicSubscriptionRuntimeState.Recovering or TopicSubscriptionRuntimeState.Failed)
                : 0;
        }
    }

    #endregion
}
