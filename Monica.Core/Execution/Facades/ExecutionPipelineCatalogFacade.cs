using Microsoft.Extensions.Logging;
using Monica.Core.Execution.Abstractions;
using Monica.Core.Execution.Models;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.Core.Execution.Facades;

/// <summary>
/// Provides result-envelope access to execution-pipeline catalog diagnostics for UI and host entry points.
/// </summary>
public sealed class ExecutionPipelineCatalogFacade(
    IExecutionPipelineCatalog catalog,
    ILogger<ExecutionPipelineCatalogFacade> logger)
{
    /// <summary>
    /// Gets all behavior registrations and observed plans for the current host.
    /// </summary>
    /// <returns>A successful immutable snapshot, or a diagnostic failure result.</returns>
    public Task<Result<ExecutionPipelineCatalogSnapshot>> GetSnapshotAsync()
    {
        try
        {
            return Task.FromResult<Result<ExecutionPipelineCatalogSnapshot>>(Result.Success(catalog.GetSnapshot()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to build the execution-pipeline catalog snapshot.");
            return Task.FromResult(Result.Failure<ExecutionPipelineCatalogSnapshot>(
                $"Failed to build the execution-pipeline catalog snapshot: {exception.GetMessageRecursively()}"));
        }
    }
}
