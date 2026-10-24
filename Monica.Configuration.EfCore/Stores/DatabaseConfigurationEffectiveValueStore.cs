using Microsoft.EntityFrameworkCore;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EfCore.Entities;
using Monica.Configuration.EfCore.Stores.Support;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.EfCore.Stores;

internal sealed class DatabaseConfigurationEffectiveValueStore(ConfigurationDatabase database)
    : IConfigurationEffectiveValueStore
{
    public bool SupportsPublishedDefinitionGuard => true;

    private const int MAX_ENSURE_RETRY_COUNT = 5;

    /// <inheritdoc />
    public ConfigurationStoreDescriptor Descriptor => ConfigurationDatabase.Descriptor;

    /// <inheritdoc />
    public async Task<ConfigurationEffectiveValueDocument> EnsureCreatedAsync(
        ConfigurationDefinition definition,
        string seedJson,
        CancellationToken cancellationToken)
    {
        var documents = await EnsureCreatedAsync(
            [new ConfigurationEffectiveValueSeed(definition, seedJson)],
            cancellationToken);
        return documents[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationEffectiveValueDocument>> EnsureCreatedAsync(
        IReadOnlyList<ConfigurationEffectiveValueSeed> seeds,
        CancellationToken cancellationToken)
    {
        if (seeds.Count == 0)
        {
            return [];
        }

        for (var attempt = 1; attempt <= MAX_ENSURE_RETRY_COUNT; attempt++)
        {
            try
            {
                return await database.ExecuteAsync(async (dbContext, token) =>
                {
                    var definitionIdentities = seeds
                        .Select(seed => ConfigurationDefinitionIdentity.Compute(seed.Definition.DefinitionKey))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    var existingRows = await dbContext.ConfigurationEffectiveValues
                        .Where(value => definitionIdentities.Contains(value.DefinitionIdentity))
                        .ToArrayAsync(token);
                    var existing = ConfigurationEffectiveValueMapper.ToEntityDictionary(existingRows);

                    var documentsByKey = new Dictionary<string, ConfigurationEffectiveValueDocument>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var entity in existing.Values)
                    {
                        documentsByKey[entity.DefinitionKey] = ConfigurationEffectiveValueMapper.ToDocument(entity);
                    }

                    foreach (var seed in seeds)
                    {
                        if (documentsByKey.ContainsKey(seed.Definition.DefinitionKey))
                        {
                            continue;
                        }

                        var entity = ConfigurationEffectiveValueEntity.Create(seed.Definition.DefinitionKey);
                        entity.Apply(
                            ConfigurationPersistenceValueConverter.NormalizeJson(seed.MaterializeSeedJson()),
                            seed.Definition.SchemaVersion,
                            DateTime.UtcNow,
                            modifierId: null,
                            modifierName: null);
                        dbContext.ConfigurationEffectiveValues.Add(entity);
                        documentsByKey[entity.DefinitionKey] = ConfigurationEffectiveValueMapper.ToDocument(entity);
                    }

                    if (dbContext.ChangeTracker.HasChanges())
                    {
                        await dbContext.SaveChangesAsync(token);
                    }

                    return seeds
                        .Select(seed => documentsByKey[seed.Definition.DefinitionKey])
                        .ToArray();
                }, cancellationToken);
            }
            catch (DbUpdateException) when (attempt < MAX_ENSURE_RETRY_COUNT)
            {
                // Another instance may have inserted missing seed documents first.
            }
        }

        throw new InvalidOperationException(
            $"Failed to ensure {seeds.Count} configuration effective value documents after {MAX_ENSURE_RETRY_COUNT} attempts.");
    }

    /// <inheritdoc />
    public async Task<ConfigurationEffectiveValueDocument?> GetAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var definitionIdentity = ConfigurationDefinitionIdentity.Compute(definitionKey);
            var entities = await dbContext.ConfigurationEffectiveValues
                .AsNoTracking()
                .Where(value => value.DefinitionIdentity == definitionIdentity)
                .OrderBy(value => value.DefinitionKey)
                .Take(2)
                .ToArrayAsync(token);
            var entity = ConfigurationEffectiveValueMapper.GetSingleEntity(entities, definitionKey);
            return entity is null ? null : ConfigurationEffectiveValueMapper.ToDocument(entity);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationEffectiveValueDocument?>> GetManyAsync(
        IReadOnlyList<string> definitionKeys,
        CancellationToken cancellationToken)
    {
        if (definitionKeys.Count == 0)
        {
            return [];
        }

        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var definitionIdentities = definitionKeys
                .Select(ConfigurationDefinitionIdentity.Compute)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var entities = await dbContext.ConfigurationEffectiveValues
                .AsNoTracking()
                .Where(value => definitionIdentities.Contains(value.DefinitionIdentity))
                .ToArrayAsync(token);
            var entitiesByKey = ConfigurationEffectiveValueMapper.ToEntityDictionary(entities);
            return definitionKeys
                .Select(definitionKey => entitiesByKey.TryGetValue(definitionKey, out var entity)
                    ? ConfigurationEffectiveValueMapper.ToDocument(entity)
                    : null)
                .ToArray();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConfigurationEffectiveValueDocument> SaveAsync(
        ConfigurationEffectiveValueSaveRequest request,
        CancellationToken cancellationToken)
    {
        return await database.ExecuteResilientAsync(async (dbContext, token) =>
        {
            await using var transaction = request.PublishedDefinitionExpectation is null ? null
                : await dbContext.Database.BeginTransactionAsync(token);
            if (transaction is not null)
            {
                await ConfigurationDatabaseLock.AcquireAsync(dbContext,
                    ConfigurationStoreLockEntity.DefinitionPublicationLockKey, token);
                await ConfigurationDatabaseLock.AcquireAsync(dbContext,
                    ConfigurationStoreLockEntity.MutationGroupsLockKey, token);
            }
            var definitionKey = request.Definition.DefinitionKey;
            var definitionIdentity = ConfigurationDefinitionIdentity.Compute(definitionKey);
            var entities = await dbContext.ConfigurationEffectiveValues
                .Where(value => value.DefinitionIdentity == definitionIdentity)
                .OrderBy(value => value.DefinitionKey)
                .Take(2)
                .ToArrayAsync(token);
            var entity = ConfigurationEffectiveValueMapper.GetSingleEntity(entities, definitionKey);
            await ConfigurationPublishedWriteGuard.ValidateAsync(dbContext, request, entity, token);
            if (request.ExpectedVersion is not null && entity?.Version != request.ExpectedVersion)
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Expected version {request.ExpectedVersion} for '{definitionKey}', but current version is {entity?.Version.ToString() ?? "<none>"}.");
            }

            if (entity is null)
            {
                entity = ConfigurationEffectiveValueEntity.Create(definitionKey);
                dbContext.ConfigurationEffectiveValues.Add(entity);
            }

            entity.Apply(
                ConfigurationPersistenceValueConverter.NormalizeJson(request.Json),
                request.Definition.SchemaVersion,
                DateTime.UtcNow,
                request.Context.ModifierId,
                request.Context.ModifierName);

            await dbContext.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return ConfigurationEffectiveValueMapper.ToDocument(entity);
        }, cancellationToken);
    }
}
