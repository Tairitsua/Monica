using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Models.Definitions;

/// <summary>
/// Projects one persisted job definition: the immutable code declaration published by its owner, the independent
/// operator policy, and the owner's latest observation state.
/// </summary>
public sealed record JobDefinition
{
    public required string SchedulerScopeKey { get; init; }

    /// <summary>
    /// Gets the stable identity of the application or deployment unit that owns and executes the job.
    /// </summary>
    public required string OwnerKey { get; init; }

    public required JobDeclaration Declaration { get; init; }
    public required JobPolicy Policy { get; init; }

    /// <summary>
    /// Gets whether the definition was present in the owner's latest published snapshot. Absent definitions remain
    /// queryable for audit and policy retention, but reject new admission.
    /// </summary>
    public bool IsPresent { get; init; }

    /// <summary>
    /// Gets when the owner's coordinator last confirmed its manifest in UTC. Stale observation indicates missing
    /// publication evidence, not proven process failure, and never blocks lease recovery by itself.
    /// </summary>
    public DateTimeOffset LastObservedAtUtc { get; init; }

    /// <summary>
    /// Gets the identity of this definition.
    /// </summary>
    public JobId Id => new(OwnerKey, Declaration.JobKey);

    /// <summary>
    /// Gets the operator-visible configuration produced from the immutable declaration and sticky policy overrides.
    /// </summary>
    public EffectiveJobConfiguration EffectiveConfiguration => Policy.Overrides.Resolve(Declaration);

    /// <summary>
    /// Gets whether the effective operator policy prevents new automatic or triggered admission.
    /// </summary>
    public bool IsDisabled => EffectiveConfiguration.IsDisabled;

    /// <summary>
    /// Captures the exact effective configuration as a durable execution template. Later declaration, policy, or
    /// schedule edits do not change work that has already entered the queue.
    /// </summary>
    public JobExecutionTemplate CreateExecutionTemplate()
    {
        var effective = EffectiveConfiguration;
        return new JobExecutionTemplate
        {
            SchedulerScopeKey = SchedulerScopeKey,
            OwnerKey = OwnerKey,
            JobKey = Declaration.JobKey,
            JobName = effective.JobName,
            JobType = Declaration.JobType,
            JobArgsKey = Declaration.JobArgsKey,
            MaxConcurrency = effective.MaxConcurrency,
            RetryCount = effective.RetryCount,
            MaxExecutionTimeout = effective.MaxExecutionTimeout
        };
    }
}

/// <summary>
/// Publishes the complete declaration set discovered by one owner host.
/// </summary>
/// <remarks>
/// Snapshot synchronization is idempotent: repeating an identical snapshot refreshes observation timestamps only.
/// Declarations missing from the latest snapshot mark their persisted definitions absent without deleting operator
/// policy, so a job that returns later resumes with its sticky overrides intact.
/// </remarks>
public sealed record JobOwnerSnapshot(
    string SchedulerScopeKey,
    string OwnerKey,
    IReadOnlyList<JobDeclaration> Declarations)
{
    /// <summary>
    /// Gets the persisted manifest hash explicitly authorized for replacement. Changed declarations are rejected
    /// unless this matches the current hash. Identical publication and first publication need no replacement hash.
    /// Set this only after incompatible writers have been drained and stopped.
    /// </summary>
    public string? ExpectedPreviousManifestHash { get; init; }

    /// <summary>Gets the automatic owner's coordinator grant, when published by the scheduling host.</summary>
    public JobCoordinatorLeaseKey? CoordinatorLease { get; init; }

    /// <summary>Computes a stable hash over the normalized, job-key-ordered complete declaration manifest.</summary>
    public string ComputeManifestHash()
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            NormalizeAndValidate().Declarations, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    internal JobOwnerSnapshot NormalizeAndValidate()
    {
        JobSchedulerIdentity.ValidateStandard(SchedulerScopeKey, nameof(SchedulerScopeKey));
        JobSchedulerIdentity.ValidateStandard(OwnerKey, nameof(OwnerKey));
        ArgumentNullException.ThrowIfNull(Declarations);
        var normalized = Declarations
            .Select(static declaration => declaration.NormalizeAndValidate())
            .OrderBy(static declaration => declaration.JobKey, StringComparer.Ordinal)
            .ToArray();
        var duplicate = normalized
            .GroupBy(static declaration => declaration.JobKey, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Job '{duplicate.Key}' appears more than once in the owner snapshot.",
                nameof(Declarations));
        }

        return this with { Declarations = normalized };
    }
}

/// <summary>
/// Reports the durable effect of one owner snapshot synchronization.
/// </summary>
public sealed record JobDefinitionSyncResult
{
    /// <summary>Gets the accepted declaration manifest hash.</summary>
    public string ManifestHash { get; init; } = string.Empty;
    /// <summary>Gets whether declaration or presence content changed, independently of owner liveness.</summary>
    public bool ContentChanged { get; init; }
    /// <summary>
    /// Gets the number of definitions present in the latest snapshot, including previously absent definitions that
    /// returned.
    /// </summary>
    public int PresentCount { get; init; }

    /// <summary>
    /// Gets the number of previously present definitions marked absent because the latest snapshot no longer
    /// declares them.
    /// </summary>
    public int MarkedAbsentCount { get; init; }

    /// <summary>
    /// Gets the store's authoritative owner observation timestamp projected onto its definitions.
    /// </summary>
    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>
/// Selects the stable ordering used for definition and operational-summary queries.
/// </summary>
public enum JobDefinitionSortField
{
    /// <summary>
    /// Sort by the operator-facing job name.
    /// </summary>
    JobName,

    /// <summary>
    /// Sort by the stable logical job key.
    /// </summary>
    JobKey,

    /// <summary>
    /// Sort by the source owner identity.
    /// </summary>
    OwnerKey,

    /// <summary>
    /// Sort by recurring or triggered job type.
    /// </summary>
    JobType,

    /// <summary>
    /// Sort by the effective disabled policy.
    /// </summary>
    IsDisabled
}

/// <summary>
/// Defines a bounded definition query with stable server-side ordering.
/// </summary>
public sealed record JobDefinitionQuery
{
    /// <summary>
    /// Gets an optional exact logical job key filter.
    /// </summary>
    public string? JobKey { get; init; }

    public string? OwnerKey { get; init; }
    public string? SearchText { get; init; }
    public JobType? JobType { get; init; }
    public bool? IsDisabled { get; init; }

    /// <summary>
    /// Gets an optional present-state filter. Absent definitions are retained for audit and are hidden only when a
    /// caller explicitly filters to present definitions.
    /// </summary>
    public bool? IsPresent { get; init; }

    /// <summary>
    /// Gets the selected server-side sort field.
    /// </summary>
    public JobDefinitionSortField SortField { get; init; } = JobDefinitionSortField.JobName;

    /// <summary>
    /// Gets whether results are returned in descending order.
    /// </summary>
    public bool SortDescending { get; init; }

    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
