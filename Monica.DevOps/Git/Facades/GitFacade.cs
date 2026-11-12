using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.DevOps.Git.Abstractions;
using Monica.DevOps.Git.Models;
using Monica.Modules;
using Monica.Core.Results;

namespace Monica.DevOps.Git.Facades;

/// <summary>
/// Facade wrapper for Git operations consumed by UI components.
/// </summary>
public sealed class GitFacade(
    IGitRepositoryService repositoryService,
    IOptions<ModuleGitOption> options,
    ILogger<GitFacade> logger)
{
    private const string ManualSyncDisabledMessage = "Manual Git synchronization is disabled by module configuration.";
    private readonly ModuleGitOption _option = options.Value;

    /// <summary>
    /// Gets all repository snapshots.
    /// </summary>
    public async Task<Result<IReadOnlyList<GitRepositoryStatus>>> GetRepositoriesAsync()
    {
        try
        {
            return Result.Success(await repositoryService.GetRepositoriesAsync());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Git repositories");
            return Result.Failure<IReadOnlyList<GitRepositoryStatus>>($"Failed to load Git repositories: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a single repository snapshot.
    /// </summary>
    public async Task<Result<GitRepositoryStatus>> GetRepositoryAsync(string repositoryId)
    {
        try
        {
            return Result.Success(await repositoryService.GetRepositoryAsync(repositoryId));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Git repository {RepositoryId}", repositoryId);
            return Result.Failure<GitRepositoryStatus>($"Failed to load Git repository: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets safe credential metadata.
    /// </summary>
    public async Task<Result<IReadOnlyList<GitCredentialInfo>>> GetCredentialsAsync()
    {
        try
        {
            return Result.Success(await repositoryService.GetCredentialsAsync());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Git credentials");
            return Result.Failure<IReadOnlyList<GitCredentialInfo>>($"Failed to load Git credentials: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets registered credential resolver metadata.
    /// </summary>
    public async Task<Result<IReadOnlyList<GitCredentialResolverInfo>>> GetCredentialResolversAsync()
    {
        try
        {
            return Result.Success(await repositoryService.GetCredentialResolversAsync());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load Git credential resolvers");
            return Result.Failure<IReadOnlyList<GitCredentialResolverInfo>>($"Failed to load Git credential resolvers: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronizes a repository.
    /// </summary>
    public async Task<Result<GitSyncResult>> SyncRepositoryAsync(string repositoryId)
    {
        if (!_option.IsSyncTriggerEnabled(GitSyncTrigger.Manual))
        {
            return Result.Failure<GitSyncResult>(ManualSyncDisabledMessage);
        }

        return await RunMutationAsync(
            () => repositoryService.SyncRepositoryAsync(repositoryId, GitSyncTrigger.Manual),
            ex => logger.LogError(ex, "Failed to synchronize Git repository {RepositoryId}", repositoryId),
            "Failed to synchronize Git repository");
    }

    /// <summary>
    /// Deletes a local repository working copy.
    /// </summary>
    public async Task<Result<GitRepositoryDeleteResult>> DeleteRepositoryAsync(string repositoryId)
    {
        return await RunMutationAsync(
            () => repositoryService.DeleteRepositoryAsync(repositoryId),
            ex => logger.LogError(ex, "Failed to delete Git repository {RepositoryId}", repositoryId),
            "Failed to delete Git repository");
    }

    /// <summary>
    /// Synchronizes all repositories.
    /// </summary>
    public async Task<Result<IReadOnlyList<GitSyncResult>>> SyncAllAsync()
    {
        if (!_option.IsSyncTriggerEnabled(GitSyncTrigger.Manual))
        {
            return Result.Failure<IReadOnlyList<GitSyncResult>>(ManualSyncDisabledMessage);
        }

        return await RunMutationAsync(
            () => repositoryService.SyncAllAsync(GitSyncTrigger.Manual),
            ex => logger.LogError(ex, "Failed to synchronize all Git repositories"),
            "Failed to synchronize all Git repositories");
    }

    private async Task<Result<TResult>> RunMutationAsync<TResult>(
        Func<Task<TResult>> operation,
        Action<Exception> logError,
        string failureMessage)
    {
        try
        {
            return Result.Success(await operation());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logError(ex);
            return Result.Failure<TResult>($"{failureMessage}: {ex.Message}");
        }
    }
}
