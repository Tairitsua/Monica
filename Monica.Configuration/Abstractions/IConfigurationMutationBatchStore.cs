using Monica.Configuration.Models;

namespace Monica.Configuration.Abstractions;

/// <summary>
/// Persists prepared configuration mutation groups through one store-owned commit boundary.
/// </summary>
/// <remarks>
/// Relational implementations must commit every effective-value document, history row, mutation group, and supplied
/// unified-version snapshot atomically. Non-transactional implementations must preserve submitted ordering and report
/// applied, failed, and skipped requests truthfully through <see cref="ConfigurationMutationBatchCommitResult"/>.
/// </remarks>
public interface IConfigurationMutationBatchStore
{
    /// <summary>Whether every item atomically checks its active-publication evidence before its value write.</summary>
    bool SupportsPublishedDefinitionGuard => false;

    /// <summary>
    /// Persists one prepared mutation group.
    /// </summary>
    /// <param name="request">The prepared commit request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The committed group and final definition documents.</returns>
    Task<ConfigurationMutationBatchCommitResult> CommitAsync(
        ConfigurationMutationBatchCommitRequest request,
        CancellationToken cancellationToken);
}
