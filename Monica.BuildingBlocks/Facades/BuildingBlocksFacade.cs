using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.BuildingBlocks.Models;
using Monica.BuildingBlocks.Services;

namespace Monica.BuildingBlocks.Facades;

/// <summary>
/// Host-facing facade for building-block inspection and diagnostics.
/// </summary>
public sealed class BuildingBlocksFacade(
    ILogger<BuildingBlocksFacade> logger,
    IServiceProvider serviceProvider)
{
    private BuildingBlockCatalogService CatalogService =>
        serviceProvider.GetRequiredService<BuildingBlockCatalogService>();

    /// <summary>
    /// Gets every discovered building block.
    /// </summary>
    /// <returns>A result envelope containing the building-block projections for the current host.</returns>
    public Task<Result<List<BuildingBlockSummary>>> GetAllAsync()
    {
        try
        {
            return Task.FromResult(Result.Success<List<BuildingBlockSummary>>(CatalogService.GetAll()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load building blocks.");
            return Task.FromResult<Result<List<BuildingBlockSummary>>>(
                Result.Failure<List<BuildingBlockSummary>>($"Failed to load building blocks: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets the current host's building-block status dashboard.
    /// </summary>
    /// <returns>A result envelope containing independent coverage, topology, and catalog-health metrics.</returns>
    public Task<Result<BuildingBlockDashboardSnapshot>> GetDashboardAsync()
    {
        try
        {
            return Task.FromResult(Result.Success(CatalogService.GetDashboard()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load the building-block dashboard.");
            return Task.FromResult<Result<BuildingBlockDashboardSnapshot>>(
                Result.Failure<BuildingBlockDashboardSnapshot>($"Failed to load the building-block dashboard: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Gets a complete typed detail projection for one building block.
    /// </summary>
    /// <param name="key">The represented CLR type's full name.</param>
    /// <param name="cancellationToken">Signals that the detail request has been cancelled.</param>
    /// <returns>A result envelope containing the detail projection.</returns>
    public async Task<Result<BuildingBlockDetail>> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await CatalogService.GetDetailAsync(key, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resolve building block {BuildingBlockKey}.", key);
            return Result.Failure<BuildingBlockDetail>($"Failed to resolve building block: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets discovered domain-event metadata.
    /// </summary>
    /// <returns>A result envelope containing domain-event metadata and representative payload structures.</returns>
    public Task<Result<List<BuildingBlockDomainEventInfo>>> GetDomainEventsAsync()
    {
        try
        {
            return Task.FromResult(Result.Success<List<BuildingBlockDomainEventInfo>>(CatalogService.GetDomainEvents()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load domain events.");
            return Task.FromResult<Result<List<BuildingBlockDomainEventInfo>>>(
                Result.Failure<List<BuildingBlockDomainEventInfo>>($"Failed to load domain events: {ex.GetMessageRecursively()}"));
        }
    }

    /// <summary>
    /// Publishes a domain event payload.
    /// </summary>
    /// <param name="eventKey">
    /// The exact full CLR identity supplied by <see cref="BuildingBlockDomainEventInfo.EventKey"/>.
    /// Legacy short type names remain supported only when they identify exactly one catalog building block.
    /// </param>
    /// <param name="eventContent">The JSON payload to deserialize as the selected event type.</param>
    /// <returns>A result envelope describing whether the event was published.</returns>
    public async Task<Result> PublishDomainEventAsync(string eventKey, JsonNode eventContent)
    {
        try
        {
            await CatalogService.PublishDomainEventAsync(eventKey, eventContent);
            return Result.Success().WithMessage($"Published domain event '{eventKey}'.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish domain event {EventKey}.", eventKey);
            return Result.Failure($"Failed to publish domain event: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets enum metadata.
    /// </summary>
    /// <param name="name">An optional short enum type name used to filter the result.</param>
    /// <returns>A result envelope containing discovered enum metadata.</returns>
    public Task<Result<List<DtoAssemblyEnumInfo>>> GetEnumInfoAsync(string? name = null)
    {
        try
        {
            return Task.FromResult(Result.Success<List<DtoAssemblyEnumInfo>>(CatalogService.GetEnumInfo(name)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load enum metadata.");
            return Task.FromResult(Result.Failure<List<DtoAssemblyEnumInfo>>($"Failed to load enum metadata: {ex.GetMessageRecursively()}"));
        }
    }

}
