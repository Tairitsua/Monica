using Microsoft.EntityFrameworkCore;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EfCore.DbContext;
using Monica.Configuration.EfCore.Entities;
using Monica.Configuration.EfCore.Stores.Support;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.EfCore.Stores;

/// <summary>
/// Persists and reads published configuration definition metadata.
/// </summary>
internal sealed class DatabaseConfigurationMetadataStore(ConfigurationDatabase database)
    : IConfigurationMetadataStore, IConfigurationDefinitionMaintenanceStore
{
    private const int MAX_PUBLISH_RETRY_COUNT = 5;
    private const int PUBLISH_RETRY_BASE_DELAY_MS = 25;

    /// <inheritdoc />
    public ConfigurationStoreDescriptor Descriptor => ConfigurationDatabase.Descriptor;

    /// <inheritdoc />
    public async Task PublishAsync(
        ConfigurationDefinitionPublicationBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var candidate = PublishedDefinitionBatchCandidate.Create(batch);

        Exception? lastException = null;
        for (var attempt = 1; attempt <= MAX_PUBLISH_RETRY_COUNT; attempt++)
        {
            try
            {
                await PublishBatchWithLockAsync(candidate, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsPublishRetryableException(ex))
            {
                lastException = ex;
                if (attempt == MAX_PUBLISH_RETRY_COUNT)
                {
                    break;
                }

                await DelayPublishRetryAsync(attempt, cancellationToken);
            }
        }

        var diagnostics = await BuildPublishFailureDiagnosticsAsync(candidate.Definitions, cancellationToken);
        throw new InvalidOperationException(
            $"Failed to publish {batch.Publications.Count} configuration definition(s) after {MAX_PUBLISH_RETRY_COUNT} attempts. {diagnostics}",
            lastException);
    }

    /// <inheritdoc />
    public Task RetirePublisherAsync(
        ConfigurationPublisherIdentity publisher,
        CancellationToken cancellationToken)
    {
        return PublishAsync(
            ConfigurationDefinitionPublicationBatch.Create(publisher, []),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationPublishedDefinitionEntry>> ListPublishedDefinitionEntriesAsync(
        CancellationToken cancellationToken)
    {
        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var entities = await dbContext.ConfigurationDefinitions
                .AsNoTracking()
                .OrderBy(definition => definition.DisplayName)
                .ThenBy(definition => definition.DefinitionKey)
                .ToArrayAsync(token);
            var activeDefinitionIdentities = (await dbContext.ConfigurationDefinitionPublisherStates
                    .AsNoTracking()
                    .Select(static state => state.DefinitionIdentity)
                    .Distinct()
                    .ToArrayAsync(token))
                .ToHashSet(StringComparer.Ordinal);
            return ToPublishedDefinitionEntries(entities, activeDefinitionIdentities, token);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConfigurationPublishedDefinitionEntry?> GetPublishedDefinitionEntryAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var definitionIdentity = ConfigurationDefinitionIdentity.Compute(definitionKey);
            var entities = await dbContext.ConfigurationDefinitions
                .AsNoTracking()
                .Where(definition => definition.DefinitionIdentity == definitionIdentity)
                .OrderBy(definition => definition.DefinitionKey)
                .Take(2)
                .ToArrayAsync(token);
            var isActive = await dbContext.ConfigurationDefinitionPublisherStates
                .AsNoTracking()
                .AnyAsync(state => state.DefinitionIdentity == definitionIdentity, token);
            var activeDefinitionIdentities = isActive
                ? new HashSet<string>(StringComparer.Ordinal) { definitionIdentity }
                : new HashSet<string>(StringComparer.Ordinal);
            return ToPublishedDefinitionEntries(entities, activeDefinitionIdentities, token).FirstOrDefault();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConfigurationDefinitionPublicationOverview> GetDefinitionPublicationOverviewAsync(
        string definitionKey,
        int limit,
        CancellationToken cancellationToken)
    {
        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var normalizedLimit = Math.Clamp(limit, 1, 200);
            var definitionIdentity = ConfigurationDefinitionIdentity.Compute(definitionKey);
            var definition = await dbContext.ConfigurationDefinitions
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.DefinitionIdentity == definitionIdentity, token)
                ?? throw new KeyNotFoundException(
                    $"Published configuration definition '{definitionKey}' was not found.");
            var histories = await dbContext.ConfigurationDefinitionPublishHistories
                .AsNoTracking()
                .Where(history => history.DefinitionIdentity == definitionIdentity)
                .OrderByDescending(history => history.DefinitionRevision)
                .ThenByDescending(history => history.HistoryId)
                .Take(normalizedLimit)
                .ToArrayAsync(token);
            var publisherStates = await dbContext.ConfigurationDefinitionPublisherStates
                .AsNoTracking()
                .Where(state => state.DefinitionIdentity == definitionIdentity)
                .OrderBy(state => state.PublisherKey)
                .ThenBy(state => state.PublisherIdentity)
                .ToArrayAsync(token);

            if (!Enum.TryParse<ConfigurationReloadBehavior>(
                    definition.ReloadBehavior,
                    ignoreCase: false,
                    out var reloadBehavior)
                || !Enum.IsDefined(reloadBehavior))
            {
                throw new InvalidDataException(
                    $"Published definition '{definition.DefinitionKey}' uses unsupported reload behavior "
                    + $"'{definition.ReloadBehavior}'.");
            }

            return new ConfigurationDefinitionPublicationOverview
            {
                DefinitionKey = definition.DefinitionKey,
                LifecycleState = publisherStates.Length == 0
                    ? ConfigurationDefinitionLifecycleState.Retired
                    : ConfigurationDefinitionLifecycleState.Active,
                DefinitionRevision = definition.DefinitionRevision,
                SchemaVersion = definition.SchemaVersion,
                ReloadBehavior = reloadBehavior,
                PublisherStates = publisherStates
                    .Select(PublishedDefinitionPublisherStateCandidate.Materialize)
                    .ToArray(),
                RevisionHistories = histories
                    .Select(ConfigurationHistoryMapper.ToPublishHistory)
                    .ToArray()
            };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>>
        GetDefinitionPublisherStatesAsync(
            IReadOnlyCollection<string> definitionKeys,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitionKeys);
        var normalizedKeysByIdentity = definitionKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim())
            .GroupBy(ConfigurationDefinitionIdentity.Compute, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static key => key, StringComparer.Ordinal).First(),
                StringComparer.Ordinal);
        if (normalizedKeysByIdentity.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>(
                StringComparer.OrdinalIgnoreCase);
        }

        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var requestedIdentities = normalizedKeysByIdentity.Keys.ToArray();
            var entities = await dbContext.ConfigurationDefinitionPublisherStates
                .AsNoTracking()
                .Where(state => requestedIdentities.Contains(state.DefinitionIdentity))
                .OrderBy(state => state.DefinitionIdentity)
                .ThenBy(state => state.PublisherKey)
                .ThenBy(state => state.PublisherIdentity)
                .ToArrayAsync(token);
            var result = normalizedKeysByIdentity.Values.ToDictionary(
                static key => key,
                static _ => (IReadOnlyList<ConfigurationDefinitionPublisherState>)[],
                StringComparer.OrdinalIgnoreCase);

            foreach (var group in entities.GroupBy(static entity => entity.DefinitionIdentity, StringComparer.Ordinal))
            {
                if (!normalizedKeysByIdentity.TryGetValue(group.Key, out var definitionKey))
                {
                    continue;
                }

                result[definitionKey] = group
                    .Select(PublishedDefinitionPublisherStateCandidate.Materialize)
                    .ToArray();
            }

            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConfigurationDefinitionPurgePreview> PreviewDefinitionPurgeAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        var definitionIdentity = ConfigurationDefinitionIdentity.Compute(definitionKey);
        return database.ExecuteAsync(
            (dbContext, token) => BuildPurgePreviewAsync(dbContext, definitionIdentity, definitionKey, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task PurgeDefinitionAsync(
        ConfigurationDefinitionPurgeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definitionIdentity = ConfigurationDefinitionIdentity.Compute(request.DefinitionKey);
        var definitionWasObserved = false;

        _ = await database.ExecuteResilientAsync(async (dbContext, token) =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

            // Publication, mutation, and unified-version operations each own one lock. Taking them in stable order
            // makes the lifecycle recheck and current-value deletion one indivisible maintenance operation.
            await ConfigurationDatabaseLock.AcquireAsync(
                dbContext,
                ConfigurationStoreLockEntity.DefinitionPublicationLockKey,
                token);
            await ConfigurationDatabaseLock.AcquireAsync(
                dbContext,
                ConfigurationStoreLockEntity.MutationGroupsLockKey,
                token);
            await ConfigurationDatabaseLock.AcquireAsync(
                dbContext,
                ConfigurationStoreLockEntity.UnifiedVersionsLockKey,
                token);

            var definition = await dbContext.ConfigurationDefinitions
                .SingleOrDefaultAsync(candidate => candidate.DefinitionIdentity == definitionIdentity, token);
            if (definition is null)
            {
                if (!definitionWasObserved)
                {
                    throw new KeyNotFoundException(
                        $"Published configuration definition '{request.DefinitionKey}' was not found.");
                }

                // A retry after an ambiguous commit observes the already-completed purge as success.
                await transaction.RollbackAsync(token);
                return true;
            }

            definitionWasObserved = true;
            var activePublisherKeys = await dbContext.ConfigurationDefinitionPublisherStates
                .AsNoTracking()
                .Where(state => state.DefinitionIdentity == definitionIdentity)
                .Select(static state => state.PublisherKey)
                .Distinct()
                .OrderBy(static publisherKey => publisherKey)
                .ToArrayAsync(token);
            if (activePublisherKeys.Length != 0)
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Configuration definition '{definition.DefinitionKey}' is active and cannot be purged. "
                    + $"Current publishers: {string.Join(", ", activePublisherKeys)}.");
            }

            if (definition.DefinitionRevision != request.ExpectedDefinitionRevision)
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Expected definition revision {request.ExpectedDefinitionRevision} for "
                    + $"'{definition.DefinitionKey}', but current revision is {definition.DefinitionRevision}.");
            }

            var publicationHistories = await dbContext.ConfigurationDefinitionPublishHistories
                .Where(history => history.DefinitionIdentity == definitionIdentity)
                .ToArrayAsync(token);
            var effectiveValues = await dbContext.ConfigurationEffectiveValues
                .Where(value => value.DefinitionIdentity == definitionIdentity)
                .ToArrayAsync(token);

            dbContext.ConfigurationDefinitionPublishHistories.RemoveRange(publicationHistories);
            dbContext.ConfigurationEffectiveValues.RemoveRange(effectiveValues);
            dbContext.ConfigurationDefinitions.Remove(definition);
            await dbContext.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        }, cancellationToken);
    }

    private async Task PublishBatchWithLockAsync(
        PublishedDefinitionBatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        await database.ExecuteResilientAsync(async (dbContext, token) =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
            await ConfigurationDatabaseLock.AcquireAsync(
                dbContext,
                ConfigurationStoreLockEntity.DefinitionPublicationLockKey,
                token);
            await PublishBatchAsync(dbContext, candidate, token);
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(token);
            }

            await transaction.CommitAsync(token);
            return true;
        }, cancellationToken);
    }

    private static async Task PublishBatchAsync(
        ConfigurationDbContext dbContext,
        PublishedDefinitionBatchCandidate candidate,
        CancellationToken cancellationToken)
    {
        var currentPublisherStates = await dbContext.ConfigurationDefinitionPublisherStates
            .Where(state => state.PublisherIdentity == candidate.PublisherIdentity)
            .ToArrayAsync(cancellationToken);
        currentPublisherStates = currentPublisherStates
            .Where(state => !candidate.ExcludedDefinitionIdentities.Contains(state.DefinitionIdentity))
            .ToArray();
        var affectedIdentities = currentPublisherStates
            .Select(static state => state.DefinitionIdentity)
            .Concat(candidate.DefinitionIdentities)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (affectedIdentities.Length == 0)
        {
            return;
        }

        var currentDefinitions = await dbContext.ConfigurationDefinitions
            .Where(definition => affectedIdentities.Contains(definition.DefinitionIdentity))
            .ToArrayAsync(cancellationToken);
        if (candidate.MatchesCurrentPublisherAndCanonicalEnvelope(currentPublisherStates, currentDefinitions))
        {
            return;
        }

        var publisherStates = await dbContext.ConfigurationDefinitionPublisherStates
            .Where(state => affectedIdentities.Contains(state.DefinitionIdentity))
            .ToArrayAsync(cancellationToken);
        var statesByPublisher = publisherStates.ToDictionary(
            static state => (state.DefinitionIdentity, state.PublisherIdentity));

        // A publication is a complete logical-service snapshot. Missing rows therefore withdraw that publisher's
        // earlier evidence, while observations from other services remain active until explicitly retired.
        foreach (var staleState in currentPublisherStates
                     .Where(state => !candidate.ContainsDefinition(state.DefinitionIdentity)))
        {
            dbContext.ConfigurationDefinitionPublisherStates.Remove(staleState);
        }

        foreach (var stateCandidate in candidate.PublisherStates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (stateCandidate.DefinitionIdentity, stateCandidate.PublisherIdentity);
            if (statesByPublisher.TryGetValue(key, out var currentState))
            {
                stateCandidate.ApplyTo(currentState);
                continue;
            }

            var createdState = stateCandidate.CreateEntity();
            dbContext.ConfigurationDefinitionPublisherStates.Add(createdState);
            statesByPublisher[key] = createdState;
        }

        var definitionsByIdentity = currentDefinitions.ToDictionary(
            static definition => definition.DefinitionIdentity,
            StringComparer.Ordinal);

        // Aggregate after tracked additions and removals so the canonical definition changes only when the
        // cross-service result changes, not whenever a different service happens to publish last.
        foreach (var definitionIdentity in affectedIdentities.OrderBy(
                     static identity => identity,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveReloadBehavior = ConfigurationReloadBehaviorObservation.Aggregate(
                statesByPublisher.Values
                    .Where(state =>
                        string.Equals(state.DefinitionIdentity, definitionIdentity, StringComparison.Ordinal)
                        && dbContext.Entry(state).State != EntityState.Deleted)
                    .Select(PublishedDefinitionPublisherStateCandidate.MaterializeObservation));

            definitionsByIdentity.TryGetValue(definitionIdentity, out var current);
            PublishedDefinitionCandidate definitionCandidate;
            if (candidate.ContainsDefinition(definitionIdentity))
            {
                definitionCandidate = candidate.GetDefinition(definitionIdentity, effectiveReloadBehavior);
            }
            else if (current is not null)
            {
                definitionCandidate = PublishedDefinitionCandidate.FromCurrent(current, effectiveReloadBehavior);
            }
            else
            {
                // An orphaned observation cannot materialize a trustworthy definition envelope.
                continue;
            }

            if (current is null)
            {
                const int initialDefinitionRevision = 1;
                var initialSchemaVersion = definitionCandidate.ResolveNewSchemaVersion(
                    null,
                    ConfigurationDefinitionPublishChangeKind.Created);
                var created = definitionCandidate.CreateEntity(initialSchemaVersion, initialDefinitionRevision);
                dbContext.ConfigurationDefinitions.Add(created);
                dbContext.ConfigurationDefinitionPublishHistories.Add(
                    definitionCandidate.CreateHistory(
                        null,
                        ConfigurationDefinitionPublishChangeKind.Created,
                        candidate.Publisher,
                        initialSchemaVersion,
                        initialDefinitionRevision));
                definitionsByIdentity[created.DefinitionIdentity] = created;
                continue;
            }

            if (definitionCandidate.Matches(current))
            {
                continue;
            }

            var changeKind = definitionCandidate.HasSameSchema(current)
                ? ConfigurationDefinitionPublishChangeKind.MetadataChanged
                : ConfigurationDefinitionPublishChangeKind.SchemaChanged;
            var schemaVersion = definitionCandidate.ResolveNewSchemaVersion(current, changeKind);
            var definitionRevision = checked(Math.Max(current.DefinitionRevision, 0) + 1);
            var history = definitionCandidate.CreateHistory(
                current,
                changeKind,
                candidate.Publisher,
                schemaVersion,
                definitionRevision);
            definitionCandidate.ApplyTo(current, schemaVersion, definitionRevision);
            dbContext.ConfigurationDefinitionPublishHistories.Add(history);
        }
    }

    private async Task<string> BuildPublishFailureDiagnosticsAsync(
        IReadOnlyList<PublishedDefinitionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        try
        {
            return await database.ExecuteAsync(async (dbContext, token) =>
            {
                var definitionIdentities = candidates
                    .Select(static candidate => candidate.DefinitionIdentity)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var currentByKey = await dbContext.ConfigurationDefinitions
                    .AsNoTracking()
                    .Where(definition => definitionIdentities.Contains(definition.DefinitionIdentity))
                    .ToDictionaryAsync(
                        definition => definition.DefinitionKey,
                        StringComparer.OrdinalIgnoreCase,
                        token);
                var diagnostics = candidates
                    .Take(5)
                    .Select(candidate =>
                    {
                        currentByKey.TryGetValue(candidate.DefinitionKey, out var current);
                        var currentSchemaHash = current?.SchemaHash ?? "<missing>";
                        var currentFromProject = current?.FromProject ?? "<missing>";
                        var currentCategory = current?.Category ?? "<missing>";
                        return $"Definition='{candidate.DefinitionKey}', CurrentSchemaHash='{currentSchemaHash}', "
                               + $"CandidateSchemaHash='{candidate.SchemaHash}', CurrentFromProject='{currentFromProject}', "
                               + $"CandidateFromProject='{candidate.FromProject}', CurrentCategory='{currentCategory}', "
                               + $"CandidateCategory='{candidate.Category ?? "<null>"}'";
                    });
                return string.Join("; ", diagnostics);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Failed to collect publish diagnostics: {ex.Message}";
        }
    }

    private static async Task<ConfigurationDefinitionPurgePreview> BuildPurgePreviewAsync(
        ConfigurationDbContext dbContext,
        string definitionIdentity,
        string requestedDefinitionKey,
        CancellationToken cancellationToken)
    {
        var definition = await dbContext.ConfigurationDefinitions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.DefinitionIdentity == definitionIdentity, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Published configuration definition '{requestedDefinitionKey}' was not found.");
        var activePublisherKeys = await dbContext.ConfigurationDefinitionPublisherStates
            .AsNoTracking()
            .Where(state => state.DefinitionIdentity == definitionIdentity)
            .Select(static state => state.PublisherKey)
            .Distinct()
            .OrderBy(static publisherKey => publisherKey)
            .ToArrayAsync(cancellationToken);
        var effectiveValueVersion = await dbContext.ConfigurationEffectiveValues
            .AsNoTracking()
            .Where(value => value.DefinitionIdentity == definitionIdentity)
            .Select(static value => (long?)value.Version)
            .SingleOrDefaultAsync(cancellationToken);
        var publicationHistoryCount = await dbContext.ConfigurationDefinitionPublishHistories
            .AsNoTracking()
            .CountAsync(history => history.DefinitionIdentity == definitionIdentity, cancellationToken);
        var retainedValueHistoryCount = await dbContext.ConfigurationValueHistories
            .AsNoTracking()
            .CountAsync(history => history.DefinitionIdentity == definitionIdentity, cancellationToken);
        var retainedMutationGroupCount = await dbContext.ConfigurationValueHistories
            .AsNoTracking()
            .Where(history => history.DefinitionIdentity == definitionIdentity && history.MutationGroupId != null)
            .Select(static history => history.MutationGroupId)
            .Distinct()
            .CountAsync(cancellationToken);
        var retainedUnifiedVersionCount = await dbContext.ConfigurationUnifiedVersionDocuments
            .AsNoTracking()
            .Where(document => document.DefinitionIdentity == definitionIdentity)
            .Select(static document => document.Version)
            .Distinct()
            .CountAsync(cancellationToken);

        return new ConfigurationDefinitionPurgePreview
        {
            DefinitionKey = definition.DefinitionKey,
            DisplayName = definition.DisplayName,
            LifecycleState = activePublisherKeys.Length == 0
                ? ConfigurationDefinitionLifecycleState.Retired
                : ConfigurationDefinitionLifecycleState.Active,
            DefinitionRevision = definition.DefinitionRevision,
            ActivePublisherKeys = activePublisherKeys,
            IsLocallyRegistered = false,
            HasEffectiveValue = effectiveValueVersion is not null,
            EffectiveValueVersion = effectiveValueVersion,
            PublicationHistoryCount = publicationHistoryCount,
            RetainedValueHistoryCount = retainedValueHistoryCount,
            RetainedMutationGroupCount = retainedMutationGroupCount,
            RetainedUnifiedVersionCount = retainedUnifiedVersionCount
        };
    }

    private static ConfigurationPublishedDefinitionEntry ToPublishedDefinitionEntry(
        ConfigurationDefinitionEntity entity,
        ConfigurationDefinitionLifecycleState lifecycleState)
    {
        return ConfigurationPublishedDefinitionEntry.Materialize(new ConfigurationPublishedDefinitionRecord
        {
            StoreKey = ConfigurationDatabase.Descriptor.StoreKey,
            DefinitionKey = entity.DefinitionKey,
            LifecycleState = lifecycleState,
            SectionPath = entity.SectionPath,
            DisplayName = entity.DisplayName,
            Description = entity.Description,
            ClrTypeName = entity.ClrTypeName,
            FromProject = entity.FromProject,
            Category = entity.Category,
            SchemaVersion = entity.SchemaVersion,
            DefinitionRevision = entity.DefinitionRevision,
            SchemaHash = entity.SchemaHash,
            ReloadBehavior = entity.ReloadBehavior,
            SchemaJson = entity.SchemaJson
        });
    }

    private static IReadOnlyList<ConfigurationPublishedDefinitionEntry> ToPublishedDefinitionEntries(
        IReadOnlyList<ConfigurationDefinitionEntity> entities,
        IReadOnlySet<string> activeDefinitionIdentities,
        CancellationToken cancellationToken)
    {
        var entries = new List<ConfigurationPublishedDefinitionEntry>(entities.Count);
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(ToPublishedDefinitionEntry(
                entity,
                activeDefinitionIdentities.Contains(entity.DefinitionIdentity)
                    ? ConfigurationDefinitionLifecycleState.Active
                    : ConfigurationDefinitionLifecycleState.Retired));
        }

        var duplicateKeys = entries
            .GroupBy(static entry => entry.Metadata.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return entries
            .Select(entry => duplicateKeys.Contains(entry.Metadata.DefinitionKey)
                ? ConfigurationPublishedDefinitionEntry.FromEnvelopeFailure(
                    entry.Metadata,
                    new InvalidDataException(
                        $"Multiple persisted definition metadata rows claim key '{entry.Metadata.DefinitionKey}'."),
                    ConfigurationDefinitionMetadataRepairAction.RepairMetadataStore)
                : entry)
            .ToArray();
    }

    private static bool IsPublishRetryableException(Exception exception)
    {
        return ContainsException<DbUpdateException>(exception);
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    private static Task DelayPublishRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var jitter = Random.Shared.Next(0, PUBLISH_RETRY_BASE_DELAY_MS);
        var delay = TimeSpan.FromMilliseconds(PUBLISH_RETRY_BASE_DELAY_MS * attempt + jitter);
        return Task.Delay(delay, cancellationToken);
    }
}
