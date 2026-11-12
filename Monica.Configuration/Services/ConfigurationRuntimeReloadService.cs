using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Microsoft.Extensions.DependencyInjection;

namespace Monica.Configuration.Services;

/// <summary>
/// Builds runtime reload status reports and coordinates manual runtime reloads.
/// </summary>
internal sealed class ConfigurationRuntimeReloadService(
    IConfigurationDefinitionRegistry definitionRegistry,
    IServiceProvider services,
    IConfigurationReloadCoordinator reloadCoordinator,
    MonicaConfigurationProviderAccessor providerAccessor)
    : IConfigurationRuntimeReloadService
{
    /// <inheritdoc />
    public async Task<ConfigurationReloadStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<ConfigurationDefinitionReloadStatus>();
        var definitions = definitionRegistry.GetAll().Where(static definition => definition.Kind == ConfigurationKind.Managed)
            .OrderBy(static definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        var effectiveValueStore = definitions.Length == 0 ? null : services.GetRequiredService<IConfigurationEffectiveValueStore>();
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await effectiveValueStore!.GetAsync(definition.DefinitionKey, cancellationToken);
            var runtimeVersion = providerAccessor.Provider?.GetLoadedVersion(definition.DefinitionKey);
            statuses.Add(new ConfigurationDefinitionReloadStatus
            {
                DefinitionKey = definition.DefinitionKey,
                DisplayName = definition.DisplayName,
                Category = definition.Category,
                RuntimeLoadedVersion = runtimeVersion,
                EffectiveStoreVersion = document?.Version,
                EffectiveLastModifiedAt = document?.LastModifiedTime,
                Status = ResolveStatus(runtimeVersion, document?.Version)
            });
        }

        return new ConfigurationReloadStatusReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            EffectiveValueStore = effectiveValueStore?.Descriptor,
            Runtime = providerAccessor.Provider?.GetReloadState() ?? new ConfigurationRuntimeReloadState(),
            Definitions = statuses
        };
    }

    /// <inheritdoc />
    public async Task<ConfigurationReloadStatusReport> ReloadAsync(CancellationToken cancellationToken)
    {
        await reloadCoordinator.ReloadRuntimeConfigurationAsync(cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    private static ConfigurationReloadVersionStatus ResolveStatus(long? runtimeVersion, long? storeVersion)
    {
        return (runtimeVersion, storeVersion) switch
        {
            (null, null) => ConfigurationReloadVersionStatus.Unknown,
            (null, _) => ConfigurationReloadVersionStatus.NotLoaded,
            (_, null) => ConfigurationReloadVersionStatus.MissingEffectiveValue,
            ({ } runtime, { } store) when runtime == store => ConfigurationReloadVersionStatus.Current,
            ({ } runtime, { } store) when runtime < store => ConfigurationReloadVersionStatus.Stale,
            ({ } runtime, { } store) when runtime > store => ConfigurationReloadVersionStatus.Ahead,
            _ => ConfigurationReloadVersionStatus.Unknown
        };
    }
}
