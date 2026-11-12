using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Localization;
using Monica.Core;
using Monica.Core.Extensions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Models;
using Monica.Core.Modularity.Services;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Modules;
using Monica.StateStore.UI.Models;
using Monica.StateStore.UI.Services.Browser;
using Monica.Core.Results;
using Monica.StateStore.Abstractions;
using Monica.StateStore.UI.Localization;

namespace Monica.StateStore.UI.Services;

/// <summary>
/// UI service for state store provider discovery and dashboard operations.
/// </summary>
public class StateStoreUIService(
    IServiceProvider serviceProvider,
    MonicaApplication application,
    IOptions<ModuleStateStoreOption> stateStoreOption,
    IEnumerable<IStateStoreBrowserApi> browserApis,
    IStringLocalizer<StateStoreResource> localizer,
    ILogger<StateStoreUIService> logger)
{
    private readonly IReadOnlyList<IStateStoreBrowserApi> _browserApis = browserApis.ToList();
    private IReadOnlyList<ModuleRuntimeSnapshot>? _providerSnapshots;

    private IReadOnlyList<ModuleRuntimeSnapshot> ProviderSnapshots =>
        _providerSnapshots ??= application.Modules.GetModuleProviders(typeof(ModuleStateStore));

    #region Provider Discovery

    public Result<List<StateStoreProviderInfo>> GetRegisteredProviders()
    {
        try
        {
            var providers = new List<StateStoreProviderInfo>();
            var keyedServiceKeys = application.Modules.GetKeyedServiceKeys(typeof(ModuleStateStore));

            var defaultProvider = serviceProvider.GetService<IStateStore>();
            if (defaultProvider != null)
            {
                providers.Add(CreateProviderInfo(null, defaultProvider));
            }

            foreach (var key in keyedServiceKeys)
            {
                try
                {
                    var keyedProvider = serviceProvider.GetKeyedService<IStateStore>(key);
                    if (keyedProvider != null)
                    {
                        providers.Add(CreateProviderInfo(key, keyedProvider));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to resolve keyed state store provider: {Key}", key);
                }
            }

            return Result.Success(providers
                .OrderByDescending(provider => provider.IsDefaultStateStore)
                .ThenBy(provider => provider.ServiceKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get registered state store providers.");
            return Result.Failure<List<StateStoreProviderInfo>>(BuildDetailedErrorMessage("Service:Errors:ProviderList", ex));
        }
    }

    public Result<IStateStore> GetProvider(string? serviceKey)
    {
        try
        {
            IStateStore? provider = serviceKey == null
                ? serviceProvider.GetService<IStateStore>()
                : serviceProvider.GetKeyedService<IStateStore>(serviceKey);

            if (provider == null)
            {
                return Result.Failure<IStateStore>(localizer["Service:Errors:ProviderNotFound", serviceKey ?? localizer["Service:Labels:DefaultProvider"]]);
            }

            return Result.Success(provider);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get state store provider: {ServiceKey}", serviceKey);
            return Result.Failure<IStateStore>(BuildDetailedErrorMessage("Service:Errors:ProviderLoad", ex));
        }
    }

    private StateStoreProviderInfo CreateProviderInfo(string? serviceKey, IStateStore provider)
    {
        var (providerType, capabilities, providerSnapshot) = GetProviderMetadata(provider);
        var browserApi = GetBrowserApi(providerType, provider);
        var browserFeatures = browserApi.GetFeatures(provider);
        var defaultSearchMode = browserApi.GetDefaultSearchMode(provider);

        var isDefaultStateStore = false;
        if (serviceKey == null)
        {
            var useDistributed = stateStoreOption.Value.UseDistributedProviderAsDefault;
            var isDistributed = provider is IDistributedStateStore;
            isDefaultStateStore = useDistributed == isDistributed;
        }

        return new StateStoreProviderInfo
        {
            ServiceKey = serviceKey,
            ProviderType = providerType,
            Capabilities = capabilities,
            BrowserFeatures = browserFeatures,
            DefaultSearchMode = defaultSearchMode,
            IsDistributed = provider is IDistributedStateStore,
            IsDefaultStateStore = isDefaultStateStore,
            ImplementationType = provider.GetType().Name,
            OptionDiagnosticsTarget = providerSnapshot is null
                ? null
                : new ModuleOptionDiagnosticsTarget(
                    providerSnapshot.ModuleKey,
                    serviceKey is null
                        ? ModuleOptionProfileSelector.Default
                        : ModuleOptionProfileSelector.NamedOrDefault(serviceKey))
        };
    }

    private (
        EStateStoreProviderType ProviderType,
        EStateStoreCapabilities Capabilities,
        ModuleRuntimeSnapshot? ProviderSnapshot) GetProviderMetadata(IStateStore provider)
    {
        var providerTypeName = provider.GetType().FullName ?? string.Empty;

        foreach (var snapshot in ProviderSnapshots)
        {
            if (snapshot.ModuleInstance is not IStateStoreModuleProvider moduleProvider)
            {
                continue;
            }

            if (providerTypeName.Contains(moduleProvider.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                return (
                    moduleProvider.ProviderType,
                    moduleProvider.Capabilities,
                    snapshot);
            }
        }

        if (provider is IMemoryStateStore)
        {
            return (EStateStoreProviderType.Memory,
                EStateStoreCapabilities.KeyScanning | EStateStoreCapabilities.BulkOperations,
                null);
        }

        return (EStateStoreProviderType.Unknown, EStateStoreCapabilities.BulkOperations, null);
    }

    private IStateStoreBrowserApi GetBrowserApi(EStateStoreProviderType providerType, IStateStore provider)
    {
        return _browserApis.First(api => api.CanHandle(providerType, provider));
    }

    #endregion

    #region Key Operations

    public async Task<Result<KeyScanResult>> ScanKeysAsync(
        string? serviceKey,
        string pattern = "*",
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var browseResult = await SearchKeysAsync(serviceKey, new StateStoreKeyBrowseRequest
        {
            Query = pattern,
            Limit = limit,
            SearchMode = EStateStoreKeySearchMode.PatternScan
        }, cancellationToken);

        if (browseResult.IsFailure())
        {
            return Result.FromFailure<KeyScanResult>(browseResult);
        }

        var data = browseResult.Data
            ?? throw new InvalidOperationException("The state store key browse result was missing its data.");
        return Result.Success(new KeyScanResult
        {
            Keys = data.Items.Select(item => item.Key).ToList(),
            TotalCount = data.TotalCount,
            HasMore = data.HasMore
        });
    }

    public async Task<Result<StateStoreKeyBrowseResult>> SearchKeysAsync(
        string? serviceKey,
        StateStoreKeyBrowseRequest request,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure<StateStoreKeyBrowseResult>(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            var (providerType, _, _) = GetProviderMetadata(provider);
            var browserApi = GetBrowserApi(providerType, provider);
            return Result.Success(await browserApi.BrowseAsync(provider, request, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to search keys for query: {Query}", request.Query);
            return Result.Failure<StateStoreKeyBrowseResult>(BuildDetailedErrorMessage("Service:Errors:KeySearch", ex));
        }
    }

    public async Task<Result<StateStoreKeyInfo>> GetKeyAsync(
        string? serviceKey,
        string key,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure<StateStoreKeyInfo>(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            var (providerType, _, _) = GetProviderMetadata(provider);
            var browserApi = GetBrowserApi(providerType, provider);
            return Result.Success(await browserApi.LoadKeyAsync(provider, key, cancellationToken));
        }
        catch (KeyNotFoundException)
        {
            return Result.Failure<StateStoreKeyInfo>(localizer["Service:Errors:KeyNotFound", key]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load state store key: {Key}", key);
            return Result.Failure<StateStoreKeyInfo>(BuildDetailedErrorMessage("Service:Errors:KeyLoad", ex));
        }
    }

    public async Task<Result<bool>> KeyExistsAsync(
        string? serviceKey,
        string key,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure<bool>(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            return Result.Success(await provider.ExistAsync(key, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check whether key exists: {Key}", key);
            return Result.Failure<bool>(BuildDetailedErrorMessage("Service:Errors:KeyCheck", ex));
        }
    }

    public async Task<Result> SaveKeyAsync(
        string? serviceKey,
        StateStoreKeyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            var value = ParseRequestValue(request);

            if (!string.IsNullOrEmpty(request.ETag))
            {
                var (success, _) = await provider.TrySaveStateWithETagAsync(
                    request.Key,
                    value,
                    request.ETag,
                    cancellationToken,
                    request.TTL);

                if (!success)
                {
                    return Result.Failure(localizer["Service:Errors:ETagMismatch"]);
                }
            }
            else
            {
                await provider.SaveStateAsync(request.Key, value, cancellationToken, request.TTL);
            }

            return Result.Success().WithMessage(localizer["Service:Success:KeySaved"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save state store key: {Key}", request.Key);
            return Result.Failure(BuildDetailedErrorMessage("Service:Errors:KeySave", ex));
        }
    }

    public async Task<Result<StateStoreKeyInfo>> CreateKeyAsync(
        string? serviceKey,
        StateStoreKeyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure<StateStoreKeyInfo>(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            var value = ParseRequestValue(request);
            var created = await provider.TrySaveStateIfNotExistsAsync(
                request.Key,
                value,
                cancellationToken,
                request.TTL);

            if (!created)
            {
                return Result.Failure<StateStoreKeyInfo>(localizer["Service:Errors:KeyExists"]);
            }

            var createdKey = await GetKeyAsync(serviceKey, request.Key, cancellationToken);
            var loadedKey = createdKey.Data;
            if (createdKey.IsSuccess() && loadedKey is not null)
            {
                return Result.Success(loadedKey);
            }

            return Result.Success(new StateStoreKeyInfo
            {
                Key = request.Key,
                RawValue = request.Value,
                IsValueLoaded = true,
                Error = createdKey.Message ?? localizer["Service:Errors:KeyLoad"]
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create state store key: {Key}", request.Key);
            return Result.Failure<StateStoreKeyInfo>(BuildDetailedErrorMessage("Service:Errors:KeyCreate", ex));
        }
    }

    public async Task<Result> DeleteKeyAsync(
        string? serviceKey,
        string key,
        string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            if (!string.IsNullOrEmpty(etag))
            {
                var success = await provider.TryDeleteStateWithETagAsync(key, etag, cancellationToken);
                if (!success)
                {
                    return Result.Failure(localizer["Service:Errors:ETagMismatch"]);
                }
            }
            else
            {
                await provider.DeleteStateAsync(key, cancellationToken);
            }

            return Result.Success().WithMessage(localizer["Service:Success:KeyDeleted"]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete state store key: {Key}", key);
            return Result.Failure(BuildDetailedErrorMessage("Service:Errors:KeyDelete", ex));
        }
    }

    public async Task<Result> DeleteKeysAsync(
        string? serviceKey,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        var providerResult = GetProvider(serviceKey);
        if (providerResult.IsFailure())
        {
            return Result.FromFailure(providerResult);
        }

        var provider = providerResult.Data
            ?? throw new InvalidOperationException("The state store provider was missing from a successful result.");
        try
        {
            await provider.DeleteBulkStateAsync(keys, cancellationToken);
            return Result.Success().WithMessage(localizer["Service:Success:KeysDeleted", keys.Count]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete state store keys in bulk.");
            return Result.Failure(BuildDetailedErrorMessage("Service:Errors:KeysDelete", ex));
        }
    }

    #endregion

    private string BuildDetailedErrorMessage(string operationKey, Exception ex)
    {
        return localizer["Service:Errors:OperationDetail", localizer[operationKey], ex.GetMessageRecursively()];
    }

    private static object? ParseRequestValue(StateStoreKeyUpdateRequest request)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<object>(request.Value);
        }
        catch
        {
            return request.Value;
        }
    }
}
