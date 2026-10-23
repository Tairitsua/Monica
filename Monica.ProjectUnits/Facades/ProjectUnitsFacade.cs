using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.ProjectUnits.Models;
using Monica.ProjectUnits.Services;

namespace Monica.ProjectUnits.Facades;

/// <summary>
/// Host-facing facade for project-unit inspection and diagnostics.
/// </summary>
public sealed class ProjectUnitsFacade(
    ILogger<ProjectUnitsFacade> logger,
    IServiceProvider serviceProvider)
{
    private ProjectUnitCatalogService CatalogService =>
        serviceProvider.GetRequiredService<ProjectUnitCatalogService>();

    /// <summary>
    /// Gets every discovered project unit.
    /// </summary>
    /// <returns>A result envelope containing the project-unit projections for the current host.</returns>
    public Task<Res<List<ProjectUnitSummary>>> GetAllProjectUnitsAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok<List<ProjectUnitSummary>>(CatalogService.GetAllProjectUnits()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load project units.");
            return Task.FromResult<Res<List<ProjectUnitSummary>>>(
                Res.Fail($"Failed to load project units: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets the current host's project-unit status dashboard.
    /// </summary>
    /// <returns>A result envelope containing independent coverage, topology, and catalog-health metrics.</returns>
    public Task<Res<ProjectUnitDashboardSnapshot>> GetDashboardAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok(CatalogService.GetDashboard()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the project-unit dashboard.");
            return Task.FromResult<Res<ProjectUnitDashboardSnapshot>>(
                Res.Fail($"Failed to load the project-unit dashboard: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets a complete typed detail projection for one project unit.
    /// </summary>
    /// <param name="key">The represented CLR type's full name.</param>
    /// <param name="cancellationToken">Signals that the detail request has been cancelled.</param>
    /// <returns>A result envelope containing the detail projection.</returns>
    public async Task<Res<ProjectUnitDetail>> GetProjectUnitDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Res.Ok(await CatalogService.GetProjectUnitDetailAsync(key, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve project unit {ProjectUnitKey}.", key);
            return Res.Fail($"Failed to resolve project unit: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets discovered domain-event metadata.
    /// </summary>
    /// <returns>A result envelope containing domain-event metadata and representative payload structures.</returns>
    public Task<Res<List<ProjectUnitDomainEventInfo>>> GetDomainEventsAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok<List<ProjectUnitDomainEventInfo>>(CatalogService.GetDomainEvents()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load domain events.");
            return Task.FromResult<Res<List<ProjectUnitDomainEventInfo>>>(
                Res.Fail($"Failed to load domain events: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Publishes a domain event payload.
    /// </summary>
    /// <param name="eventKey">
    /// The exact full CLR identity supplied by <see cref="ProjectUnitDomainEventInfo.EventKey"/>.
    /// Legacy short type names remain supported only when they identify exactly one catalog unit.
    /// </param>
    /// <param name="eventContent">The JSON payload to deserialize as the selected event type.</param>
    /// <returns>A result envelope describing whether the event was published.</returns>
    public async Task<Res> PublishDomainEventAsync(string eventKey, JsonNode eventContent)
    {
        try
        {
            var eventToPublish = await CatalogService.PublishDomainEventAsync(eventKey, eventContent);
            return Res.Ok(eventToPublish).AppendMessage($"Published domain event '{eventKey}'.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish domain event {EventKey}.", eventKey);
            return Res.Fail($"Failed to publish domain event: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets a read-only snapshot of the current host's request-filter service and disabled paths.
    /// </summary>
    /// <returns>
    /// A result envelope that distinguishes an unavailable service from an available service with no disabled paths.
    /// Reading this snapshot does not attach middleware, change paths, or grant mutation access.
    /// </returns>
    public Task<Res<ProjectUnitRequestFilterState>> GetRequestFilterStateAsync()
    {
        try
        {
            return Task.FromResult(Res.Ok(CatalogService.GetRequestFilterState()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read request-filter state.");
            return Task.FromResult<Res<ProjectUnitRequestFilterState>>(
                Res.Fail($"Failed to read request-filter state: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Reads and updates the request-filter state.
    /// </summary>
    /// <param name="urls">Request paths to update, or <see langword="null"/> to query the current state.</param>
    /// <param name="disable"><see langword="true"/> to disable the paths; <see langword="false"/> to enable them.</param>
    /// <returns>
    /// A result envelope containing the currently disabled request paths, or an empty list if no service is registered.
    /// Use <see cref="GetRequestFilterStateAsync"/> when availability must be distinguished from an empty set.
    /// </returns>
    public Task<Res<List<string>>> ManageRequestFilterAsync(List<string>? urls, bool? disable)
    {
        try
        {
            return Task.FromResult(Res.Ok<List<string>>(CatalogService.ManageRequestFilter(urls, disable)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to manage request filters.");
            return Task.FromResult<Res<List<string>>>($"Failed to manage request filters: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets enum metadata.
    /// </summary>
    /// <param name="name">An optional short enum type name used to filter the result.</param>
    /// <returns>A result envelope containing discovered enum metadata.</returns>
    public Task<Res<List<DtoAssemblyEnumInfo>>> GetEnumInfoAsync(string? name = null)
    {
        try
        {
            return Task.FromResult(Res.Ok<List<DtoAssemblyEnumInfo>>(CatalogService.GetEnumInfo(name)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load enum metadata.");
            return Task.FromResult<Res<List<DtoAssemblyEnumInfo>>>($"Failed to load enum metadata: {ex.GetMessageRecursively()}");
        }
    }

}
