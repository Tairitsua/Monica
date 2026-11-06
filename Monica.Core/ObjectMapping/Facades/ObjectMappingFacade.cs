using Microsoft.Extensions.Logging;
using Monica.Core.ObjectMapping.Models;
using Monica.Core.ObjectMapping.Services;
using Monica.Core.Results;

namespace Monica.Core.ObjectMapping.Facades;

/// <summary>
/// Exposes host-owned object-mapping diagnostics to API and UI consumers.
/// </summary>
public sealed class ObjectMappingFacade
{
    private readonly ILogger<ObjectMappingFacade> _logger;
    private readonly ObjectMappingStatusService _statusService;

    internal ObjectMappingFacade(
        ObjectMappingStatusService statusService,
        ILogger<ObjectMappingFacade> logger)
    {
        _statusService = statusService;
        _logger = logger;
    }

    /// <summary>
    /// Gets the mapping pairs and generated expressions registered in the current host.
    /// </summary>
    /// <returns>A result containing the current immutable mapping snapshot.</returns>
    public Task<Result<ObjectMapperStatusResponse>> GetStatusAsync()
    {
        try
        {
            var status = _statusService.GetStatus();
            _logger.LogDebug("Retrieved object mapping status with {Count} mappings.", status.Count);
            return Task.FromResult(Result.Success(status));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to retrieve object mapping status.");
            return Task.FromResult(Result.Failure<ObjectMapperStatusResponse>(
                $"Failed to retrieve object mapping status: {exception.Message}"));
        }
    }
}
