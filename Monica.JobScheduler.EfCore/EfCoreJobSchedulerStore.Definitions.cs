using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Exceptions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore;

/// <summary>
/// Implements the owner-snapshot definition half of the relational scheduler store.
/// </summary>
public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public Task<JobDefinitionSyncResult> SyncOwnerSnapshotAsync(
        JobOwnerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = snapshot.NormalizeAndValidate();
        normalized.CoordinatorLease?.ValidatePartition(normalized.SchedulerScopeKey, "owner:" + normalized.OwnerKey);
        return WriteAsync(async (dbContext, token) =>
        {
            var owner = await LockOwnerAsync(dbContext, normalized.SchedulerScopeKey, normalized.OwnerKey, token);
            var now = await GetUtcNowAsync(dbContext, token);
            var manifestHash = normalized.ComputeManifestHash();
            if (owner is not null && owner.ManifestHash != manifestHash
                && owner.ManifestHash != normalized.ExpectedPreviousManifestHash)
            {
                throw new InvalidOperationException($"Owner '{normalized.OwnerKey}' rejected an incompatible declaration manifest. Stop incompatible writers and authorize its previous hash before replacement.");
            }
            if (owner is not null && owner.ManifestHash == manifestHash)
            {
                owner.LastObservedAtUtcTicks = ToTicks(now);
                return new JobDefinitionSyncResult
                {
                    ManifestHash = manifestHash, ContentChanged = false,
                    PresentCount = normalized.Declarations.Count, ObservedAtUtc = now
                };
            }
            if (owner is null)
            {
                owner = new JobSchedulerOwnerEntity
                {
                    SchedulerScopeKey = normalized.SchedulerScopeKey, OwnerKey = normalized.OwnerKey,
                    ManifestHash = manifestHash, ManifestPublishedAtUtcTicks = ToTicks(now),
                    LastObservedAtUtcTicks = ToTicks(now)
                };
                dbContext.SchedulerOwners.Add(owner);
                // Establish the owner mutex before touching definitions, including concurrent first publication.
                await dbContext.SaveChangesAsync(token);
            }
            owner.ManifestHash = manifestHash;
            owner.ManifestPublishedAtUtcTicks = ToTicks(now);
            owner.LastObservedAtUtcTicks = ToTicks(now);
            // Publication is rare and owner-scoped. Lock existing gates in one sorted statement, avoiding one
            // round trip per declaration. New gates are protected by the already established owner mutex.
            if (UsesNativeRowLocks(dbContext))
                await dbContext.ExecutionGates.FromSqlInterpolated(
                    $"SELECT * FROM \"JobExecutionGates\" WHERE \"SchedulerScopeKey\" = {normalized.SchedulerScopeKey} AND \"OwnerKey\" = {normalized.OwnerKey} ORDER BY \"JobKey\" FOR UPDATE").ToListAsync(token);
            else
                await dbContext.ExecutionGates.Where(row => row.SchedulerScopeKey == normalized.SchedulerScopeKey
                    && row.OwnerKey == normalized.OwnerKey).OrderBy(row => row.JobKey).ToListAsync(token);
            var existingEntities = await dbContext.Definitions
                .Where(item => item.SchedulerScopeKey == normalized.SchedulerScopeKey
                               && item.OwnerKey == normalized.OwnerKey)
                .ToListAsync(token);
            var existingByJobKey = existingEntities.ToDictionary(
                static item => item.JobKey,
                StringComparer.Ordinal);
            var existingCursors = (await dbContext.RecurringCursors
                .Where(item => item.SchedulerScopeKey == normalized.SchedulerScopeKey
                               && item.OwnerKey == normalized.OwnerKey)
                .ToListAsync(token))
                .ToDictionary(static item => item.JobKey, StringComparer.Ordinal);

            var markedAbsent = 0;
            var presentKeys = normalized.Declarations
                .Select(static declaration => declaration.JobKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var entity in existingEntities.Where(item => !presentKeys.Contains(item.JobKey)))
            {
                if (entity.IsPresent)
                {
                    markedAbsent++;
                    entity.IsPresent = false;
                    entity.LastObservedAtUtcTicks = ToTicks(now);
                }
                if (existingCursors.Remove(entity.JobKey, out var cursor))
                {
                    dbContext.RecurringCursors.Remove(cursor);
                }
            }

            foreach (var declaration in normalized.Declarations)
            {
                JobDefinitionEntity entity;
                if (existingByJobKey.TryGetValue(declaration.JobKey, out var existing))
                {
                    entity = existing;
                    if (entity.IsPresent && entity.DeclarationJson == Serialize(declaration))
                    {
                        continue;
                    }
                }
                else
                {
                    entity = new JobDefinitionEntity
                    {
                        SchedulerScopeKey = normalized.SchedulerScopeKey,
                        OwnerKey = normalized.OwnerKey,
                        JobKey = declaration.JobKey,
                        PolicyOverridesJson = Serialize(new JobPolicyOverrides()),
                        PolicyConcurrencyStamp = NewToken(),
                        PolicyUpdatedAtUtcTicks = ToTicks(now),
                        ConcurrencyToken = NewVersion()
                    };
                    dbContext.Definitions.Add(entity);
                }

                // Declaration fields follow the code; operator policy is sticky and never overwritten by a sync.
                entity.DeclarationJson = Serialize(declaration);
                entity.IsPresent = true;
                entity.LastObservedAtUtcTicks = ToTicks(now);
                if (declaration.JobType != JobType.Recurring
                    && existingCursors.Remove(declaration.JobKey, out var cursor))
                {
                    dbContext.RecurringCursors.Remove(cursor);
                }
                var definition = ToDefinition(entity, declaration);
                ApplyProjection(entity, definition);
                await ApplyEffectiveConcurrencyAsync(dbContext, definition, token);
            }

            return new JobDefinitionSyncResult
            {
                ManifestHash = manifestHash,
                ContentChanged = true,
                PresentCount = normalized.Declarations.Count,
                MarkedAbsentCount = markedAbsent,
                ObservedAtUtc = now
            };
        }, cancellationToken, normalized.CoordinatorLease);
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
        return ReadAsync(async (dbContext, token) =>
        {
            var entity = await dbContext.Definitions.AsNoTracking().SingleOrDefaultAsync(
                item => item.SchedulerScopeKey == schedulerScopeKey
                        && item.OwnerKey == ownerKey
                        && item.JobKey == jobKey,
                token);
            if (entity is null) return null;
            var observed = await dbContext.SchedulerOwners.AsNoTracking()
                .Where(row => row.SchedulerScopeKey == schedulerScopeKey && row.OwnerKey == ownerKey)
                .Select(row => (long?)row.LastObservedAtUtcTicks).SingleOrDefaultAsync(token);
            return ToDefinition(entity) with { LastObservedAtUtc = FromTicks(observed ?? entity.LastObservedAtUtcTicks) };
        }, cancellationToken);
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
        return ReadAsync(async (dbContext, token) =>
        {
            var filtered = FilterDefinitions(dbContext, schedulerScopeKey, query);
            var totalCount = await filtered.CountAsync(token);
            var ordered = OrderDefinitions(filtered, query);
            var entities = await ordered
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToListAsync(token);
            var owners = entities.Select(row => row.OwnerKey).Distinct().ToArray();
            var observed = await dbContext.SchedulerOwners.AsNoTracking()
                .Where(row => row.SchedulerScopeKey == schedulerScopeKey && owners.Contains(row.OwnerKey))
                .ToDictionaryAsync(row => row.OwnerKey, row => row.LastObservedAtUtcTicks, token);
            return new QueryResult<JobDefinition>(entities.Select(row => ToDefinition(row) with
            {
                LastObservedAtUtc = FromTicks(observed.GetValueOrDefault(row.OwnerKey, row.LastObservedAtUtcTicks))
            }).ToList(), totalCount);
        }, cancellationToken);
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
        return WriteJobAsync(schedulerScopeKey, ownerKey, jobKey, async (dbContext, token) =>
        {
            var now = await GetUtcNowAsync(dbContext, token);
            var entity = await dbContext.Definitions.SingleOrDefaultAsync(
                item => item.SchedulerScopeKey == schedulerScopeKey
                        && item.OwnerKey == ownerKey
                        && item.JobKey == jobKey,
                token)
                ?? throw new JobDefinitionNotFoundException(
                    $"Job '{ownerKey}/{jobKey}' was not found in scope '{schedulerScopeKey}'.");
            var current = ToPolicy(entity);
            var normalizedOverrides = change.Overrides.Normalize();
            if (!string.Equals(current.ConcurrencyStamp, change.ExpectedConcurrencyStamp, StringComparison.Ordinal))
            {
                throw new JobPolicyConcurrencyException(jobKey);
            }

            var declaration = Deserialize<JobDeclaration>(entity.DeclarationJson);
            normalizedOverrides.Validate(declaration, current.Overrides);

            var replacement = new JobPolicy
            {
                Overrides = normalizedOverrides,
                ConcurrencyStamp = NewToken(),
                UpdatedAtUtc = now
            };
            entity.PolicyOverridesJson = Serialize(normalizedOverrides);
            entity.PolicyConcurrencyStamp = replacement.ConcurrencyStamp;
            entity.PolicyUpdatedAtUtcTicks = ToTicks(now);
            var definition = ToDefinition(entity, declaration) with { Policy = replacement };
            ApplyProjection(entity, definition);
            await ApplyEffectiveConcurrencyAsync(dbContext, definition, token);
            await ApplyPolicyToRecurringCursorAsync(dbContext, definition, now, token);
            return replacement;
        }, cancellationToken);
    }

    private static IQueryable<JobDefinitionEntity> FilterDefinitions(
        JobSchedulerDbContext dbContext,
        string schedulerScopeKey,
        JobDefinitionQuery query)
    {
        var filtered = dbContext.Definitions.AsNoTracking()
            .Where(item => item.SchedulerScopeKey == schedulerScopeKey);
        if (!string.IsNullOrWhiteSpace(query.JobKey))
        {
            filtered = filtered.Where(item => item.JobKey == query.JobKey);
        }
        if (!string.IsNullOrWhiteSpace(query.OwnerKey))
        {
            filtered = filtered.Where(item => item.OwnerKey == query.OwnerKey);
        }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var search = query.SearchText.ToLower();
            filtered = filtered.Where(item =>
                item.JobKey.ToLower().Contains(search)
                || item.JobName.ToLower().Contains(search)
                || (item.Description != null && item.Description.ToLower().Contains(search)));
        }
        if (query.JobType is { } jobType)
        {
            filtered = filtered.Where(item => item.JobType == jobType);
        }
        if (query.IsDisabled is { } isDisabled)
        {
            filtered = filtered.Where(item => item.IsDisabled == isDisabled);
        }
        if (query.IsPresent is { } isPresent)
        {
            filtered = filtered.Where(item => item.IsPresent == isPresent);
        }

        return filtered;
    }

    private static IOrderedQueryable<JobDefinitionEntity> OrderDefinitions(
        IQueryable<JobDefinitionEntity> filtered,
        JobDefinitionQuery query)
    {
        var ordered = query.SortField switch
        {
            JobDefinitionSortField.JobName => Order(filtered, query, item => item.JobName),
            JobDefinitionSortField.JobKey => Order(filtered, query, item => item.JobKey),
            JobDefinitionSortField.OwnerKey => Order(filtered, query, item => item.OwnerKey),
            JobDefinitionSortField.JobType => Order(filtered, query, item => item.JobType),
            JobDefinitionSortField.IsDisabled => Order(filtered, query, item => item.IsDisabled),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.SortField, "Unsupported sort field.")
        };
        return ordered
            .ThenBy(item => item.OwnerKey)
            .ThenBy(item => item.JobKey);
    }

    private static IOrderedQueryable<JobDefinitionEntity> Order<TKey>(
        IQueryable<JobDefinitionEntity> filtered,
        JobDefinitionQuery query,
        System.Linq.Expressions.Expression<Func<JobDefinitionEntity, TKey>> selector) =>
        query.SortDescending
            ? filtered.OrderByDescending(selector)
            : filtered.OrderBy(selector);

    private Task ApplyEffectiveConcurrencyAsync(
        JobSchedulerDbContext dbContext,
        JobDefinition definition,
        CancellationToken cancellationToken)
    {
        // Every caller has already loaded/locked the relevant Gate before changing its definition.
        cancellationToken.ThrowIfCancellationRequested();
        var gate = dbContext.ExecutionGates.Local.SingleOrDefault(
            item => item.SchedulerScopeKey == definition.SchedulerScopeKey
                    && item.OwnerKey == definition.OwnerKey
                    && item.JobKey == definition.Declaration.JobKey);
        if (gate is null)
        {
            gate = new JobExecutionGateEntity
            {
                SchedulerScopeKey = definition.SchedulerScopeKey,
                OwnerKey = definition.OwnerKey,
                JobKey = definition.Declaration.JobKey,
                ConcurrencyToken = NewVersion()
            };
            dbContext.ExecutionGates.Add(gate);
        }

        gate.MaxConcurrency = definition.EffectiveConfiguration.MaxConcurrency;
        return Task.CompletedTask;
    }

    private static JobDefinition ToDefinition(JobDefinitionEntity entity)
    {
        return ToDefinition(entity, Deserialize<JobDeclaration>(entity.DeclarationJson));
    }

    private static JobDefinition ToDefinition(JobDefinitionEntity entity, JobDeclaration declaration) => new()
    {
        SchedulerScopeKey = entity.SchedulerScopeKey,
        OwnerKey = entity.OwnerKey,
        Declaration = declaration,
        Policy = ToPolicy(entity),
        IsPresent = entity.IsPresent,
        LastObservedAtUtc = FromTicks(entity.LastObservedAtUtcTicks)
    };

    private static JobPolicy ToPolicy(JobDefinitionEntity entity) => new()
    {
        Overrides = Deserialize<JobPolicyOverrides>(entity.PolicyOverridesJson),
        ConcurrencyStamp = entity.PolicyConcurrencyStamp,
        UpdatedAtUtc = FromTicks(entity.PolicyUpdatedAtUtcTicks)
    };

    private static void ApplyProjection(JobDefinitionEntity entity, JobDefinition definition)
    {
        var effective = definition.EffectiveConfiguration;
        entity.JobName = effective.JobName;
        entity.Description = effective.Description;
        entity.JobType = definition.Declaration.JobType;
        entity.IsDisabled = effective.IsDisabled;
    }
}
