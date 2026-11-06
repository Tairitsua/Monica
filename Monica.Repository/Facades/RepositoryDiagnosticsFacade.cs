using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;

namespace Monica.Repository.Facades;

/// <summary>
/// UI-facing Repository diagnostics entry point.
/// </summary>
public sealed class RepositoryDiagnosticsFacade(
    IRepositoryDbContextDiagnosticsService diagnosticsService)
{
    /// <summary>
    /// Gets runtime snapshots for all registered Repository DbContexts.
    /// </summary>
    /// <param name="revealConnectionStrings">Whether to include full connection strings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Registered DbContext snapshots.</returns>
    public async Task<Result<IReadOnlyList<RepositoryDbContextSnapshot>>> GetRegisteredContextsAsync(
        bool revealConnectionStrings = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await diagnosticsService.GetRegisteredContextsAsync(revealConnectionStrings, cancellationToken));
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<RepositoryDbContextSnapshot>>($"Failed to load Repository DbContext diagnostics: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets activity rows for one registered Repository DbContext.
    /// </summary>
    /// <param name="contextId">Selected DbContext identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Activity result.</returns>
    public async Task<Result<RepositoryActivityResult>> GetActivityAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await diagnosticsService.GetActivityAsync(contextId, cancellationToken));
        }
        catch (Exception ex)
        {
            return Result.Failure<RepositoryActivityResult>($"Failed to load Repository activity diagnostics: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Gets migration status for one registered Repository DbContext.
    /// </summary>
    /// <param name="contextId">Selected DbContext identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Migration status.</returns>
    public async Task<Result<RepositoryMigrationStatus>> GetMigrationStatusAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await diagnosticsService.GetMigrationStatusAsync(contextId, cancellationToken));
        }
        catch (Exception ex)
        {
            return Result.Failure<RepositoryMigrationStatus>($"Failed to load Repository migration status: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Applies pending migrations for one registered Repository DbContext.
    /// </summary>
    /// <param name="contextId">Selected DbContext identifier.</param>
    /// <param name="commandTimeout">Migration command timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Migration update result.</returns>
    public async Task<Result<RepositoryMigrationUpdateResult>> UpdateMigrationAsync(
        string contextId,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await diagnosticsService.UpdateMigrationAsync(contextId, commandTimeout, cancellationToken));
        }
        catch (Exception ex)
        {
            return Result.Failure<RepositoryMigrationUpdateResult>($"Failed to update Repository migrations: {ex.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Applies pending migrations for all registered Repository DbContexts.
    /// </summary>
    /// <param name="commandTimeout">Migration command timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-context update results.</returns>
    public async Task<Result<IReadOnlyList<RepositoryMigrationUpdateResult>>> UpdateAllPendingMigrationsAsync(
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Result.Success(await diagnosticsService.UpdateAllPendingMigrationsAsync(commandTimeout, cancellationToken));
        }
        catch (Exception ex)
        {
            return Result.Failure<IReadOnlyList<RepositoryMigrationUpdateResult>>($"Failed to update Repository migrations: {ex.GetMessageRecursively()}");
        }
    }
}
