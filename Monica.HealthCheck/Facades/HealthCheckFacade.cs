using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.HealthCheck.Models;
using Monica.HealthCheck.Services;

namespace Monica.HealthCheck.Facades;

/// <summary>
/// Exposes sanitized, point-in-time health snapshots to host and UI consumers.
/// </summary>
public sealed class HealthCheckFacade
{
    private readonly HealthCheckSnapshotService _snapshotService;
    private readonly ILogger<HealthCheckFacade> _logger;

    internal HealthCheckFacade(
        HealthCheckSnapshotService snapshotService,
        ILogger<HealthCheckFacade> logger)
    {
        _snapshotService = snapshotService;
        _logger = logger;
    }

    /// <summary>
    /// Executes the selected checks and returns a sanitized snapshot for the current host.
    /// </summary>
    /// <param name="scope">The registration scope to execute.</param>
    /// <param name="cancellationToken">Cancels health-check execution.</param>
    /// <returns>A successful result containing the current snapshot, or a failure result when execution cannot complete.</returns>
    public async Task<Result<HealthCheckSnapshot>> GetSnapshotAsync(
        HealthCheckScope scope = HealthCheckScope.All,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await _snapshotService.GetSnapshotAsync(scope, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute health checks for scope {Scope}", scope);
            return Result.Failure<HealthCheckSnapshot>($"Failed to execute health checks: {ex.GetMessageRecursively()}");
        }
    }
}
