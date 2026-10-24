using Monica.Configuration.Abstractions;
using Monica.Configuration.Abstractions.Internal;
using Monica.Configuration.Metrics;
using Monica.Configuration.Models;
using Monica.Configuration.Projection;
using Microsoft.Extensions.DependencyInjection;

namespace Monica.Configuration.Services.Support;

/// <summary>
/// Idempotently connects Monica's configuration projection to the final application service provider.
/// </summary>
internal sealed class MonicaConfigurationProviderActivationCoordinator(
    IServiceProvider serviceProvider,
    MonicaConfigurationProviderAccessor accessor,
    IConfigurationDefinitionRegistry definitionRegistry,
    IConfigurationMetadataStore metadataStore,
    IConfigurationEffectiveValueStore effectiveValueStore,
    ConfigurationPublisherIdentityProvider publisherIdentityProvider,
    ConfigurationDefinitionResolver definitionResolver,
    IConfigurationStoreStateTracker stateTracker,
    IConfigurationReloadCoordinator reloadCoordinator,
    ConfigurationMetricsRecorder metricsRecorder)
{
    internal static bool ShouldActivate(IConfigurationDefinitionRegistry definitions, IServiceProvider services)
    {
        var registered = definitions.GetAll();
        if (registered.Any(definition => definition.Kind == ConfigurationKind.Managed)) return true;
        return registered.Count == 0
            && services.GetService<IServiceProviderIsService>()?.IsService(typeof(IConfigurationMetadataStore)) == true;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _activated;

    /// <summary>
    /// Activates the projection provider, publishing local metadata first only in Automatic mode.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_activated)
            {
                return;
            }

            await metricsRecorder.MeasureStartupStageAsync(
                ConfigurationStartupStage.ProviderActivation,
                () => ActivateProviderAsync(cancellationToken));
            _activated = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ActivateProviderAsync(CancellationToken cancellationToken)
    {
        var autonomous = serviceProvider.GetRequiredService<ConfigurationRuntimeContext>().AutonomousWork;
        try
        {
            if (autonomous)
                await metricsRecorder.MeasureStartupStageAsync(
                    ConfigurationStartupStage.MetadataPublication,
                    () => PublishMetadataAsync(cancellationToken));
        }
        catch (Exception exception)
        {
            stateTracker.RecordFailure(metadataStore.Descriptor.StoreKey, exception);
            throw;
        }

        var storesShareHealth = string.Equals(
            metadataStore.Descriptor.StoreKey,
            effectiveValueStore.Descriptor.StoreKey,
            StringComparison.Ordinal);
        if (autonomous && !storesShareHealth)
        {
            stateTracker.RecordSuccess(metadataStore.Descriptor.StoreKey);
        }

        // Automatic activation publishes before enabling seed writes. Manual activation only reads existing
        // documents or validates temporary inputs, so it never establishes or changes publication authority.
        accessor.ServiceProvider = serviceProvider;
        await metricsRecorder.MeasureStartupStageAsync(
            ConfigurationStartupStage.ProjectionReload,
            () => reloadCoordinator.ReloadMonicaProjectionAsync(cancellationToken));

        if (autonomous && storesShareHealth)
        {
            stateTracker.RecordSuccess(metadataStore.Descriptor.StoreKey);
        }
    }

    private async Task PublishMetadataAsync(CancellationToken cancellationToken)
    {
        var publication = ConfigurationDefinitionPublicationBatch.Create(
            publisherIdentityProvider.GetIdentity(),
            definitionRegistry.GetAll());
        await metadataStore.PublishAsync(publication, cancellationToken);
        definitionResolver.InvalidateReadSnapshot();
    }

}
