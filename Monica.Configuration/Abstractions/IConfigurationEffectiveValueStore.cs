using Monica.Configuration.Models;

namespace Monica.Configuration.Abstractions;

/// <summary>
/// Persists the current effective configuration document for each managed configuration definition.
/// </summary>
public interface IConfigurationEffectiveValueStore : IConfigurationEffectiveValueReader
{
    /// <summary>Whether saves atomically enforce supplied publication evidence and require an existing document.</summary>
    /// <remarks>Custom stores default to false and cannot receive Manual mutation requests until this contract is implemented.</remarks>
    bool SupportsPublishedDefinitionGuard => false;

    /// <summary>
    /// Ensures that a definition has an effective value document.
    /// </summary>
    /// <param name="definition">The configuration definition.</param>
    /// <param name="seedJson">The JSON document used when the definition is not present yet.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The existing or newly created effective value document.</returns>
    Task<ConfigurationEffectiveValueDocument> EnsureCreatedAsync(
        ConfigurationDefinition definition,
        string seedJson,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ensures that multiple definitions have effective value documents.
    /// </summary>
    /// <param name="seeds">
    /// Definitions and lazy seed requests. Implementations must materialize a seed only after determining that the
    /// corresponding document is missing, while preserving the input order in the result.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Effective value documents in the same order as the input seeds.</returns>
    Task<IReadOnlyList<ConfigurationEffectiveValueDocument>> EnsureCreatedAsync(
        IReadOnlyList<ConfigurationEffectiveValueSeed> seeds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Saves an updated effective value document.
    /// </summary>
    /// <param name="request">The save request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The saved document.</returns>
    Task<ConfigurationEffectiveValueDocument> SaveAsync(
        ConfigurationEffectiveValueSaveRequest request,
        CancellationToken cancellationToken);
}
