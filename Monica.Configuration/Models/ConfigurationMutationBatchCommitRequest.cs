namespace Monica.Configuration.Models;

/// <summary>
/// Describes a prepared mutation group that a store must persist as one commit boundary when supported.
/// </summary>
public sealed record ConfigurationMutationBatchCommitRequest
{
    /// <summary>
    /// Gets the mutation group row to persist.
    /// </summary>
    public required ConfigurationMutationGroup MutationGroup { get; init; }

    /// <summary>
    /// Gets one final document save per definition, ordered by the definition's first command.
    /// </summary>
    public IReadOnlyList<ConfigurationMutationBatchCommitItem> Items { get; init; } = [];
}

/// <summary>
/// Couples one final aggregate save with every command and its ordered audit history.
/// Staged intermediate documents are never persistence items.
/// </summary>
public sealed record ConfigurationMutationBatchCommitItem
{
    /// <summary>
    /// Gets the caller request identities in this definition's command order.
    /// </summary>
    public required IReadOnlyList<string> RequestIds { get; init; }

    /// <summary>
    /// Gets the final document save, with the observed baseline version as its concurrency guard.
    /// </summary>
    public required ConfigurationEffectiveValueSaveRequest SaveRequest { get; init; }

    /// <summary>
    /// Gets one history row per request, preserving staging order and sharing the final document version.
    /// </summary>
    public required IReadOnlyList<ConfigurationValueHistory> Histories { get; init; }
}

/// <summary>
/// Describes a completed store-level mutation-group commit.
/// </summary>
public sealed record ConfigurationMutationBatchCommitResult
{
    /// <summary>
    /// Gets the persisted mutation group.
    /// </summary>
    public required ConfigurationMutationGroup MutationGroup { get; init; }

    /// <summary>
    /// Gets saved documents keyed by definition.
    /// </summary>
    public IReadOnlyDictionary<string, ConfigurationEffectiveValueDocument> Documents { get; init; } =
        new Dictionary<string, ConfigurationEffectiveValueDocument>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets request identities whose effective values crossed the store persistence boundary.
    /// </summary>
    public IReadOnlyList<string> AppliedRequestIds { get; init; } = [];

    /// <summary>
    /// Gets the first persistence failure for a best-effort store, when one occurred.
    /// </summary>
    public ConfigurationMutationBatchFailure? Failure { get; init; }

    /// <summary>
    /// Gets non-blocking audit failures that happened after value persistence.
    /// </summary>
    public IReadOnlyList<ConfigurationPostCommitIssue> PostCommitIssues { get; init; } = [];
}

/// <summary>
/// Identifies the first request of a failed definition save in a best-effort mutation batch.
/// Every request belonging to that batch item remains unapplied.
/// </summary>
public sealed record ConfigurationMutationBatchFailure
{
    /// <summary>
    /// Gets the request identity that failed.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Gets a concise failure message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Gets display-safe diagnostic guidance without persistence exception text or configuration values.
    /// </summary>
    public required string Detail { get; init; }
}
