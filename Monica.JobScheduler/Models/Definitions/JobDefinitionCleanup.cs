using System.Security.Cryptography;
using System.Text.Json;

namespace Monica.JobScheduler.Models.Definitions;

/// <summary>Explains a definition's current removal prerequisites; observation age never proves deletion.</summary>
public enum JobDefinitionCleanupState
{
    /// <summary>The accepted owner manifest omits the definition and no active work remains.</summary>
    Ready,
    /// <summary>Removal additionally requires explicit cancellation of queued work.</summary>
    Queued,
    /// <summary>Running work or unreleased execution capacity prevents removal.</summary>
    Running,
    /// <summary>Publication evidence is missing, or the definition remains published by an owner whose observation is stale.</summary>
    OwnerUnconfirmed,
    /// <summary>The accepted declaration still publishes the definition.</summary>
    Published,
    /// <summary>The exact definition no longer exists.</summary>
    Missing
}

/// <summary>Selects a bounded cleanup inventory. Counts exclude the state filter but include owner and search.</summary>
public sealed record JobDefinitionCleanupQuery
{
    /// <summary>Gets an optional exact owner identity, without normalization.</summary>
    public string? OwnerKey { get; init; }
    /// <summary>Gets a name, key or owner substring.</summary>
    public string? SearchText { get; init; }
    /// <summary>Gets an optional readiness filter.</summary>
    public JobDefinitionCleanupState? State { get; init; }
    /// <summary>Gets the one-based page number; the default is one.</summary>
    public int PageNumber { get; init; } = 1;
    /// <summary>Gets the page size, from one through 200; the default is 20.</summary>
    public int PageSize { get; init; } = 20;

    internal void Validate()
    {
        if (OwnerKey is not null) JobSchedulerIdentity.ValidateStandard(OwnerKey, nameof(OwnerKey));
        if (PageNumber < 1 || PageSize is < 1 or > 200 || (long)(PageNumber - 1) * PageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (State is { } state && !Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(State));
    }
}

/// <summary>Captures authoritative counts for one exact definition, rather than a page of sampled executions.</summary>
public sealed record JobDefinitionCleanupItem
{
    /// <summary>Gets the exact owner/job pair.</summary>
    public required JobId Id { get; init; }
    /// <summary>Gets the retained definition, or null when it was removed.</summary>
    public JobDefinition? Definition { get; init; }
    /// <summary>Gets the accepted owner manifest identity, or null for legacy metadata.</summary>
    public string? ManifestHash { get; init; }
    /// <summary>Gets when the accepted manifest content changed.</summary>
    public DateTimeOffset? ManifestPublishedAtUtc { get; init; }
    /// <summary>Gets the durable owner-retirement timestamp, when retired.</summary>
    public DateTimeOffset? OwnerRetiredAtUtc { get; init; }
    /// <summary>Gets the current removal classification.</summary>
    public JobDefinitionCleanupState State { get; init; }
    /// <summary>Gets all queued instances, including delayed retries.</summary>
    public int QueuedCount { get; init; }
    /// <summary>Gets all running instances, including expired leases awaiting recovery.</summary>
    public int RunningCount { get; init; }
    /// <summary>Gets unreleased capacity or execution leases; any nonzero value blocks removal.</summary>
    public int ActiveLeaseCount { get; init; }
    /// <summary>Gets the number of retained terminal executions, independently of history pagination.</summary>
    public long HistoryCount { get; init; }
    /// <summary>Gets the exact queued-identity fingerprint captured by a bounded preview; inventory reads may omit it.</summary>
    public string? QueueIdentityStamp { get; init; }
    /// <summary>Gets a concurrency fingerprint of publication, policy and active-work prerequisites.</summary>
    public string Stamp => CleanupFingerprint.Create(Id, Definition?.SchedulerScopeKey,
        Definition?.IsPresent, Definition?.AbsentSinceUtc, Definition?.Policy.ConcurrencyStamp,
        ManifestHash, ManifestPublishedAtUtc, OwnerRetiredAtUtc, QueuedCount, RunningCount, ActiveLeaseCount, QueueIdentityStamp);

    /// <summary>Determines eligibility for the explicit queue-cancellation choice.</summary>
    public bool CanRemove(bool cancelQueued) => ManifestHash is not null && (State == JobDefinitionCleanupState.Ready
        || (cancelQueued && State == JobDefinitionCleanupState.Queued));
}

/// <summary>Contains a bounded inventory and complete state counts for the selected owner/search population.</summary>
public sealed record JobDefinitionCleanupSnapshot
{
    /// <summary>Gets the page of candidate definitions.</summary>
    public required IReadOnlyList<JobDefinitionCleanupItem> Items { get; init; }
    /// <summary>Gets counts before the state filter, including candidates beyond the returned page.</summary>
    public required IReadOnlyDictionary<JobDefinitionCleanupState, int> Counts { get; init; }
    /// <summary>Gets exact owner choices in the candidate population.</summary>
    public required IReadOnlyList<string> Owners { get; init; }
    /// <summary>Gets the total after every filter, before pagination.</summary>
    public int TotalCount { get; init; }
    /// <summary>Gets all retained definitions in this scope.</summary>
    public int ScopeDefinitionCount { get; init; }
    /// <summary>Gets the store observation time used for freshness classification.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>Freezes one explicit selection before applying cleanup. Mixed selections retain protected items.</summary>
public sealed record JobDefinitionCleanupPlan
{
    /// <summary>Gets the reviewed identities and prerequisites, including protected or missing definitions.</summary>
    public required IReadOnlyList<JobDefinitionCleanupItem> Items { get; init; }
    /// <summary>Gets whether queued instances of eligible absent definitions should be cancelled.</summary>
    public bool CancelQueued { get; init; }
    /// <summary>Gets the exact eligible subset.</summary>
    public IReadOnlyList<JobDefinitionCleanupItem> Eligible => Items.Where(item => item.CanRemove(CancelQueued)).ToArray();
    /// <summary>Gets the total queued instances that the plan would cancel.</summary>
    public int QueuedCount => Eligible.Sum(item => item.QueuedCount);
    /// <summary>Gets retained terminal records plus the cancellation records created by this operation.</summary>
    public long HistoryCount => Eligible.Sum(item => item.HistoryCount + item.QueuedCount);
    /// <summary>Gets whether the plan fits the bounded cancellation transaction.</summary>
    public bool WithinCancellationLimit => QueuedCount <= JobDefinitionCleanupRequest.MAX_QUEUED_CANCELLATIONS;
    /// <summary>Creates an apply request that binds the reviewed eligible identities and their stamps.</summary>
    public JobDefinitionCleanupRequest ToRequest() => new()
    {
        Targets = Eligible.Select(item => new JobDefinitionCleanupTarget(item.Id, item.Stamp)).ToArray(),
        CancelQueued = CancelQueued
    };
}

/// <summary>Binds one exact identity to the prerequisites accepted in a cleanup preview.</summary>
public sealed record JobDefinitionCleanupTarget(JobId Id, string ExpectedStamp);

/// <summary>Requests one atomic bounded removal. Any changed target rejects the whole request.</summary>
public sealed record JobDefinitionCleanupRequest
{
    /// <summary>Limits definition targets per atomic request to 100.</summary>
    public const int MAX_DEFINITIONS = 100;
    /// <summary>Limits queued cancellations per transaction to 1,000; split larger selections first.</summary>
    public const int MAX_QUEUED_CANCELLATIONS = 1000;
    /// <summary>Gets the exact reviewed targets; duplicate identities are invalid.</summary>
    public required IReadOnlyList<JobDefinitionCleanupTarget> Targets { get; init; }
    /// <summary>Gets the explicit permission to cancel queued work. Running work is never terminated.</summary>
    public bool CancelQueued { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Targets);
        ValidateIds(Targets.Select(target => target.Id).ToArray());
        foreach (var target in Targets) ArgumentException.ThrowIfNullOrWhiteSpace(target.ExpectedStamp);
    }

    internal static void ValidateIds(IReadOnlyList<JobId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count is < 1 or > MAX_DEFINITIONS) throw new ArgumentOutOfRangeException(nameof(ids));
        foreach (var id in ids) id.Validate();
        if (ids.Distinct().Count() != ids.Count) throw new ArgumentException("Definition identities must be distinct.", nameof(ids));
    }
}

/// <summary>Reports whether an atomic maintenance command applied, conflicted, or was blocked.</summary>
public enum JobDefinitionMaintenanceStatus
{
    /// <summary>The command applied completely.</summary>
    Applied,
    /// <summary>A reviewed prerequisite changed; no mutation occurred.</summary>
    Changed,
    /// <summary>Current work, publication or transaction limits prevent the command.</summary>
    Blocked
}

/// <summary>Reports the exact effect of a removal or owner-retirement transaction.</summary>
public sealed record JobDefinitionMaintenanceResult
{
    /// <summary>Gets the atomic outcome.</summary>
    public required JobDefinitionMaintenanceStatus Status { get; init; }
    /// <summary>Gets removed or newly unpublished identities; empty on rejection.</summary>
    public IReadOnlyList<JobId> Definitions { get; init; } = [];
    /// <summary>Gets the number of queued instances cancelled by removal.</summary>
    public int CancelledCount { get; init; }
    /// <summary>Gets retained execution records at the accepted operation boundary.</summary>
    public long RetainedExecutionCount { get; init; }
    /// <summary>Gets the authoritative operation time.</summary>
    public DateTimeOffset AtUtc { get; init; }
}

/// <summary>Captures one owner's exact publication and activity prerequisites for deliberate retirement.</summary>
public sealed record JobOwnerRetirementPreview
{
    /// <summary>Gets the scope that owns the publication fence.</summary>
    public required string SchedulerScopeKey { get; init; }
    /// <summary>Gets the exact owner identity.</summary>
    public required string OwnerKey { get; init; }
    /// <summary>Gets the accepted manifest, or null when owner metadata is unavailable.</summary>
    public string? ManifestHash { get; init; }
    /// <summary>Gets the latest observed publication instant.</summary>
    public DateTimeOffset? LastObservedAtUtc { get; init; }
    /// <summary>Gets an existing retirement timestamp; retired owners reject all later publication.</summary>
    public DateTimeOffset? RetiredAtUtc { get; init; }
    /// <summary>Gets the number of retained definitions that retirement would mark absent.</summary>
    public int DefinitionCount { get; init; }
    /// <summary>Gets the queued instances retained for a later cleanup decision.</summary>
    public int QueuedCount { get; init; }
    /// <summary>Gets running instances, including expired claims awaiting recovery.</summary>
    public int RunningCount { get; init; }
    /// <summary>Gets unreleased execution capacity.</summary>
    public int ActiveLeaseCount { get; init; }
    /// <summary>Gets whether a publication coordinator is currently leased.</summary>
    public bool HasActivePublisher { get; init; }
    /// <summary>Gets the protected publication revision, including unchanged-manifest observations.</summary>
    public string Stamp => CleanupFingerprint.Create(SchedulerScopeKey, OwnerKey, ManifestHash, LastObservedAtUtc, RetiredAtUtc,
        DefinitionCount, QueuedCount, RunningCount, ActiveLeaseCount, HasActivePublisher);
    /// <summary>Determines whether confirmed publisher shutdown can retire this owner.</summary>
    public bool CanRetire => ManifestHash is not null && RetiredAtUtc is null && DefinitionCount > 0
        && !HasActivePublisher && RunningCount == 0 && ActiveLeaseCount == 0;
}

internal static class CleanupFingerprint
{
    internal static string Create(params object?[] fields) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
}
