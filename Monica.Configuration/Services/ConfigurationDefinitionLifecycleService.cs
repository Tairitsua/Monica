using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Services;

/// <summary>
/// Keeps lifecycle policy inside the facade while persistence remains behind public store abstractions.
/// </summary>
internal sealed class ConfigurationDefinitionLifecycleService(
    IConfigurationDefinitionRegistry definitionRegistry,
    IConfigurationDefinitionMaintenanceStore maintenanceStore,
    ConfigurationDefinitionResolver definitionResolver,
    ConfigurationRuntimeContext? runtimeContext = null)
{
    /// <summary>
    /// Gets the store-level purge impact enriched with current-process registration state.
    /// </summary>
    /// <param name="definitionKey">The stable definition key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The destructive and retained record counts.</returns>
    public async Task<ConfigurationDefinitionPurgePreview> PreviewPurgeAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        var normalizedDefinitionKey = NormalizeDefinitionKey(definitionKey);
        var preview = await maintenanceStore.PreviewDefinitionPurgeAsync(
            normalizedDefinitionKey,
            cancellationToken);
        return EnrichWithLocalRegistration(preview);
    }

    /// <summary>
    /// Treats an Automatic local definition as active before publication; Manual reads preserve persisted lifecycle.
    /// </summary>
    /// <param name="overview">The persisted publication overview.</param>
    /// <returns>The overview enriched with current-process registration state.</returns>
    public ConfigurationDefinitionPublicationOverview EnrichWithLocalRegistration(
        ConfigurationDefinitionPublicationOverview overview)
    {
        return runtimeContext?.AutonomousWork != false && definitionRegistry.TryGet(overview.DefinitionKey, out _)
            ? overview with { LifecycleState = ConfigurationDefinitionLifecycleState.Active }
            : overview;
    }

    /// <summary>
    /// Revalidates lifecycle and revision before delegating the atomic purge to the owning store.
    /// </summary>
    /// <param name="request">The reviewed definition key and revision.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes after the definition is purged.</returns>
    /// <exception cref="ConfigurationConcurrencyConflictException">
    /// The definition is locally registered, has a current publisher, or changed after preview.
    /// </exception>
    public async Task PurgeAsync(
        ConfigurationDefinitionPurgeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (runtimeContext?.MaintenanceReadOnlyReason is { } reason)
            throw new ConfigurationValidationFailedException(reason);
        var normalizedRequest = request with
        {
            DefinitionKey = NormalizeDefinitionKey(request.DefinitionKey)
        };
        if (normalizedRequest.ExpectedDefinitionRevision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConfigurationDefinitionPurgeRequest.ExpectedDefinitionRevision),
                normalizedRequest.ExpectedDefinitionRevision,
                "Expected definition revision must be greater than zero.");
        }

        var preview = EnrichWithLocalRegistration(
            await maintenanceStore.PreviewDefinitionPurgeAsync(
                normalizedRequest.DefinitionKey,
                cancellationToken));
        EnsureCanPurge(preview, normalizedRequest.ExpectedDefinitionRevision);

        await maintenanceStore.PurgeDefinitionAsync(normalizedRequest, cancellationToken);
        definitionResolver.InvalidateReadSnapshot();
    }

    private ConfigurationDefinitionPurgePreview EnrichWithLocalRegistration(
        ConfigurationDefinitionPurgePreview preview)
    {
        var isLocallyRegistered = definitionRegistry.TryGet(preview.DefinitionKey, out _);
        return preview with
        {
            IsLocallyRegistered = isLocallyRegistered,
            ReadOnlyReason = runtimeContext?.MaintenanceReadOnlyReason,
            LifecycleState = runtimeContext?.AutonomousWork != false && isLocallyRegistered
                ? ConfigurationDefinitionLifecycleState.Active
                : preview.LifecycleState
        };
    }

    private static void EnsureCanPurge(
        ConfigurationDefinitionPurgePreview preview,
        int expectedDefinitionRevision)
    {
        if (preview.DefinitionRevision != expectedDefinitionRevision)
        {
            throw new ConfigurationConcurrencyConflictException(
                $"Configuration definition '{preview.DefinitionKey}' advanced from reviewed revision "
                + $"{expectedDefinitionRevision} to {preview.DefinitionRevision}. Refresh the purge preview.");
        }

        if (preview.IsLocallyRegistered)
        {
            throw new ConfigurationConcurrencyConflictException(
                $"Configuration definition '{preview.DefinitionKey}' is registered by the current process and cannot be purged.");
        }

        if (preview.LifecycleState != ConfigurationDefinitionLifecycleState.Retired
            || preview.ActivePublisherKeys.Count != 0)
        {
            throw new ConfigurationConcurrencyConflictException(
                $"Configuration definition '{preview.DefinitionKey}' still has an active publisher and cannot be purged.");
        }
    }

    private static string NormalizeDefinitionKey(string definitionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionKey);
        return definitionKey.Trim();
    }
}
