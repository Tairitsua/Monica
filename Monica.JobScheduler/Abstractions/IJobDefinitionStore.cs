using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;

namespace Monica.JobScheduler.Abstractions;

/// <summary>
/// Persists owner-published job definitions and the independently mutable operator policy projection.
/// </summary>
/// <remarks>
/// Definitions are keyed by scheduler scope, owner, and job key. Owners self-register by synchronizing their
/// complete declaration snapshot; declarations missing from the latest snapshot become absent while their operator
/// policy is retained for audit and for a possible return.
/// </remarks>
public interface IJobDefinitionStore
{
    /// <summary>Gets the currently accepted owner manifest hash, or null before first publication.</summary>
    Task<string?> GetOwnerManifestHashAsync(string schedulerScopeKey, string ownerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically synchronizes one owner's complete declaration snapshot.
    /// </summary>
    /// <remarks>
    /// Present declarations are upserted without overwriting operator policy. Definitions of the same owner missing
    /// from the snapshot are marked absent. Repeating an identical snapshot is idempotent and only refreshes the
    /// owner observation row, without rewriting definitions or gates. A different manifest requires the exact
    /// accepted previous hash after incompatible writers have been stopped; operator policy remains independent.
    /// </remarks>
    Task<JobDefinitionSyncResult> SyncOwnerSnapshotAsync(
        JobOwnerSnapshot snapshot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one definition by its exact identity.
    /// </summary>
    /// <returns>The definition, or <see langword="null"/> when the identity is unknown.</returns>
    Task<JobDefinition?> GetDefinitionAsync(
        string schedulerScopeKey,
        string ownerKey,
        string jobKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries a page of definitions with bounded server-side ordering.
    /// </summary>
    Task<QueryResult<JobDefinition>> QueryDefinitionsAsync(
        string schedulerScopeKey,
        JobDefinitionQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces operator policy while applying its effects to the concurrency gate and any existing
    /// recurring cursor.
    /// </summary>
    /// <remarks>
    /// Schedule replacement and resume calculate the next occurrence strictly after the store's authoritative current
    /// time; queued and running execution snapshots remain unchanged.
    /// </remarks>
    /// <exception cref="JobPolicyConcurrencyException">The expected concurrency stamp no longer matches.</exception>
    /// <exception cref="JobDefinitionNotFoundException">The addressed definition does not exist.</exception>
    Task<JobPolicy> UpdatePolicyAsync(
        string schedulerScopeKey,
        string ownerKey,
        string jobKey,
        JobPolicyChange change,
        CancellationToken cancellationToken = default);
}
