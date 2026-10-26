using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Providers;

/// <summary>
/// Implements the owner-snapshot definition half of the volatile scheduler store.
/// </summary>
public sealed partial class InMemoryJobSchedulerStore
{
    private readonly Dictionary<DefinitionKey, JobDefinition> _definitions = [];

    /// <inheritdoc />
    public Task<JobDefinitionSyncResult> SyncOwnerSnapshotAsync(
        JobOwnerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = snapshot.NormalizeAndValidate();
        normalized.CoordinatorLease?.ValidatePartition(normalized.SchedulerScopeKey, "owner:" + normalized.OwnerKey);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ValidateCoordinatorLeaseUnsafe(normalized.CoordinatorLease);
            var now = UtcNow;
            var identity = (normalized.SchedulerScopeKey, normalized.OwnerKey);
            var manifestHash = normalized.ComputeManifestHash();
            var hasOwner = _owners.TryGetValue(identity, out var owner);
            if (hasOwner && owner.Hash != manifestHash && owner.Hash != normalized.ExpectedPreviousManifestHash)
                throw new InvalidOperationException($"Owner '{normalized.OwnerKey}' rejected an incompatible declaration manifest.");
            _owners[identity] = (manifestHash, now,
                hasOwner && owner.Hash == manifestHash ? owner.Published : now);
            if (hasOwner && owner.Hash == manifestHash)
            {
                return Task.FromResult(new JobDefinitionSyncResult
                {
                    ManifestHash = manifestHash, ContentChanged = false,
                    PresentCount = normalized.Declarations.Count, ObservedAtUtc = now
                });
            }
            var presentKeys = normalized.Declarations
                .Select(static declaration => declaration.JobKey)
                .ToHashSet(StringComparer.Ordinal);
            var existingDefinitions = _definitions.Values
                .Where(definition => string.Equals(
                    definition.SchedulerScopeKey,
                    normalized.SchedulerScopeKey,
                    StringComparison.Ordinal)
                    && string.Equals(definition.OwnerKey, normalized.OwnerKey, StringComparison.Ordinal))
                .ToArray();

            var markedAbsent = 0;
            foreach (var existing in existingDefinitions.Where(definition =>
                         !presentKeys.Contains(definition.Declaration.JobKey)))
            {
                _definitions[DefinitionKeyOf(existing)] = existing with { IsPresent = false, LastObservedAtUtc = now };
                _recurringCursors.Remove(DefinitionKeyOf(existing));
                if (existing.IsPresent)
                {
                    markedAbsent++;
                }
            }

            foreach (var declaration in normalized.Declarations)
            {
                var key = new DefinitionKey(normalized.SchedulerScopeKey, normalized.OwnerKey, declaration.JobKey);
                if (_definitions.TryGetValue(key, out var existing))
                {
                    // Declaration fields follow the code; operator policy is sticky and never overwritten by a sync.
                    var updated = existing with
                    {
                        Declaration = declaration,
                        IsPresent = true,
                        LastObservedAtUtc = now
                    };
                    _definitions[key] = updated;
                    if (declaration.JobType != JobType.Recurring)
                    {
                        _recurringCursors.Remove(key);
                    }
                    ApplyEffectiveConcurrencyUnsafe(updated);
                }
                else
                {
                    var created = new JobDefinition
                    {
                        SchedulerScopeKey = normalized.SchedulerScopeKey,
                        OwnerKey = normalized.OwnerKey,
                        Declaration = declaration,
                        Policy = CreateDefaultPolicy(now),
                        IsPresent = true,
                        LastObservedAtUtc = now
                    };
                    _definitions.Add(key, created);
                    ApplyEffectiveConcurrencyUnsafe(created);
                }
            }

            return Task.FromResult(new JobDefinitionSyncResult
            {
                ManifestHash = manifestHash,
                ContentChanged = true,
                PresentCount = normalized.Declarations.Count,
                MarkedAbsentCount = markedAbsent,
                ObservedAtUtc = now
            });
        }
    }

    /// <inheritdoc />
    public Task<JobDefinition?> GetDefinitionAsync(
        string schedulerScopeKey,
        string ownerKey,
        string jobKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        JobSchedulerIdentity.ValidateJobKey(jobKey, nameof(jobKey));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var definition = _definitions.GetValueOrDefault(new DefinitionKey(schedulerScopeKey, ownerKey, jobKey));
            return Task.FromResult(definition is null ? null : WithOwnerObservationUnsafe(definition));
        }
    }

    /// <inheritdoc />
    public Task<QueryResult<JobDefinition>> QueryDefinitionsAsync(
        string schedulerScopeKey,
        JobDefinitionQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageNumber < 1 || query.PageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Page number and size must be positive.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var filtered = FilterDefinitions(GetScopeDefinitions(schedulerScopeKey), query);
            var ordered = filtered.ApplyDefinitionOrdering(query).ToArray();
            var page = ordered
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToList();
            return Task.FromResult(new QueryResult<JobDefinition>(page, ordered.Length));
        }
    }

    /// <inheritdoc />
    public Task<JobPolicy> UpdatePolicyAsync(
        string schedulerScopeKey,
        string ownerKey,
        string jobKey,
        JobPolicyChange change,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ValidateIdentity(ownerKey, nameof(ownerKey));
        JobSchedulerIdentity.ValidateJobKey(jobKey, nameof(jobKey));
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(change.Overrides);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var definition = ResolveDefinitionUnsafe(schedulerScopeKey, ownerKey, jobKey);
            var normalizedOverrides = change.Overrides.Normalize();
            if (!string.Equals(
                    definition.Policy.ConcurrencyStamp,
                    change.ExpectedConcurrencyStamp,
                    StringComparison.Ordinal))
            {
                throw new JobPolicyConcurrencyException(jobKey);
            }

            normalizedOverrides.Validate(definition.Declaration, definition.Policy.Overrides);

            var updatedAtUtc = UtcNow;
            var replacement = new JobPolicy
            {
                Overrides = normalizedOverrides,
                ConcurrencyStamp = CreateConcurrencyStamp(),
                UpdatedAtUtc = updatedAtUtc
            };
            var updated = definition with { Policy = replacement };
            _definitions[DefinitionKeyOf(definition)] = updated;
            ApplyEffectiveConcurrencyUnsafe(updated);
            ApplyPolicyToRecurringCursorUnsafe(updated, updatedAtUtc);
            return Task.FromResult(replacement);
        }
    }

    internal JobDefinition ResolveDefinitionUnsafe(string schedulerScopeKey, string ownerKey, string jobKey)
    {
        return _definitions.TryGetValue(
                   new DefinitionKey(schedulerScopeKey, ownerKey, jobKey),
                   out var definition)
            ? definition
            : throw new JobDefinitionNotFoundException(
                $"Job '{ownerKey}/{jobKey}' was not found in scope '{schedulerScopeKey}'.");
    }

    internal JobDefinition ResolvePresentDefinitionUnsafe(string schedulerScopeKey, string ownerKey, string jobKey)
    {
        var definition = ResolveDefinitionUnsafe(schedulerScopeKey, ownerKey, jobKey);
        if (!definition.IsPresent)
        {
            throw new JobDefinitionNotFoundException(
                $"Job '{ownerKey}/{jobKey}' is absent from the latest owner snapshot in scope '{schedulerScopeKey}'.");
        }

        return definition;
    }

    internal static JobPolicy CreateDefaultPolicy(DateTimeOffset now) => new()
    {
        Overrides = new JobPolicyOverrides(),
        ConcurrencyStamp = CreateConcurrencyStamp(),
        UpdatedAtUtc = now
    };

    private static string CreateConcurrencyStamp() => Guid.NewGuid().ToString("N");

    private IEnumerable<JobDefinition> GetScopeDefinitions(string schedulerScopeKey)
    {
        return _definitions.Values.Where(definition => string.Equals(
            definition.SchedulerScopeKey,
            schedulerScopeKey,
            StringComparison.Ordinal)).Select(WithOwnerObservationUnsafe);
    }

    private static IEnumerable<JobDefinition> FilterDefinitions(
        IEnumerable<JobDefinition> definitions,
        JobDefinitionQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.JobKey))
        {
            definitions = definitions.Where(item =>
                string.Equals(item.Declaration.JobKey, query.JobKey, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.OwnerKey))
        {
            definitions = definitions.Where(item =>
                string.Equals(item.OwnerKey, query.OwnerKey, StringComparison.Ordinal));
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            definitions = definitions.Where(item =>
                item.Declaration.JobKey.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
                || item.EffectiveConfiguration.JobName.Contains(query.SearchText, StringComparison.OrdinalIgnoreCase)
                || item.EffectiveConfiguration.Description?.Contains(
                    query.SearchText,
                    StringComparison.OrdinalIgnoreCase) == true);
        }
        if (query.JobType is { } jobType)
        {
            definitions = definitions.Where(item => item.Declaration.JobType == jobType);
        }
        if (query.IsDisabled is { } isDisabled)
        {
            definitions = definitions.Where(item => item.IsDisabled == isDisabled);
        }
        if (query.IsPresent is { } isPresent)
        {
            definitions = definitions.Where(item => item.IsPresent == isPresent);
        }

        return definitions;
    }

    private static DefinitionKey DefinitionKeyOf(JobDefinition definition) =>
        new(definition.SchedulerScopeKey, definition.OwnerKey, definition.Declaration.JobKey);

    internal readonly record struct DefinitionKey(string SchedulerScopeKey, string OwnerKey, string JobKey);
    private JobDefinition WithOwnerObservationUnsafe(JobDefinition definition) =>
        _owners.TryGetValue((definition.SchedulerScopeKey, definition.OwnerKey), out var owner)
            ? definition with { LastObservedAtUtc = owner.Observed } : definition;

}
