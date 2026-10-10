using Microsoft.EntityFrameworkCore;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EfCore.DbContext;
using Monica.Configuration.EfCore.Entities;
using Monica.Configuration.EfCore.Stores.Support;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.EfCore.Stores;

internal sealed class DatabaseConfigurationMutationBatchStore(ConfigurationDatabase database)
    : IConfigurationMutationBatchStore
{
    public async Task<ConfigurationMutationBatchCommitResult> CommitAsync(
        ConfigurationMutationBatchCommitRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await database.ExecuteResilientAsync(
                (dbContext, token) => CommitAttemptAsync(dbContext, request, token),
                cancellationToken);
        }
        catch
        {
            var committed = await TryGetCommittedAsync(request, cancellationToken);
            if (committed is not null)
            {
                return committed;
            }

            throw;
        }
    }

    private static async Task<ConfigurationMutationBatchCommitResult> CommitAttemptAsync(
        ConfigurationDbContext dbContext,
        ConfigurationMutationBatchCommitRequest request,
        CancellationToken cancellationToken)
    {
        var existingGroup = await FindGroupAsync(dbContext, request.MutationGroup.GroupId, cancellationToken);
        if (existingGroup is not null)
        {
            return await BuildCommittedResultAsync(dbContext, request, existingGroup, cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await ConfigurationDatabaseLock.AcquireAsync(
            dbContext,
            ConfigurationStoreLockEntity.MutationGroupsLockKey,
            cancellationToken);

        existingGroup = await FindGroupAsync(dbContext, request.MutationGroup.GroupId, cancellationToken);
        if (existingGroup is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await BuildCommittedResultAsync(dbContext, request, existingGroup, cancellationToken);
        }

        var entities = await LoadEffectiveValuesAsync(dbContext, request, trackChanges: true, cancellationToken);
        foreach (var item in request.Items)
        {
            ApplyMutation(dbContext, entities, item);
        }

        dbContext.ConfigurationMutationGroups.Add(ConfigurationHistoryMapper.ToEntity(request.MutationGroup));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConfigurationConcurrencyConflictException(
                "A configuration document changed while the mutation group was being committed.");
        }

        return CreateResult(request, request.MutationGroup, entities);
    }

    private async Task<ConfigurationMutationBatchCommitResult?> TryGetCommittedAsync(
        ConfigurationMutationBatchCommitRequest request,
        CancellationToken cancellationToken)
    {
        return await database.ExecuteAsync(async (dbContext, token) =>
        {
            var group = await FindGroupAsync(dbContext, request.MutationGroup.GroupId, token);
            return group is null
                ? null
                : await BuildCommittedResultAsync(dbContext, request, group, token);
        }, cancellationToken);
    }

    private static async Task<ConfigurationMutationBatchCommitResult> BuildCommittedResultAsync(
        ConfigurationDbContext dbContext,
        ConfigurationMutationBatchCommitRequest request,
        ConfigurationMutationGroupEntity group,
        CancellationToken cancellationToken)
    {
        var entities = await LoadEffectiveValuesAsync(dbContext, request, trackChanges: false, cancellationToken);
        return CreateResult(request, ConfigurationHistoryMapper.ToGroup(group), entities);
    }

    private static async Task<ConfigurationMutationGroupEntity?> FindGroupAsync(
        ConfigurationDbContext dbContext,
        string groupId,
        CancellationToken cancellationToken)
    {
        return await dbContext.ConfigurationMutationGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(group => group.GroupId == groupId, cancellationToken);
    }

    private static async Task<Dictionary<string, ConfigurationEffectiveValueEntity>> LoadEffectiveValuesAsync(
        ConfigurationDbContext dbContext,
        ConfigurationMutationBatchCommitRequest request,
        bool trackChanges,
        CancellationToken cancellationToken)
    {
        var definitionIdentities = request.Items
            .Select(item => item.SaveRequest.Definition.DefinitionKey)
            .Select(ConfigurationDefinitionIdentity.Compute)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var query = trackChanges
            ? dbContext.ConfigurationEffectiveValues
            : dbContext.ConfigurationEffectiveValues.AsNoTracking();
        var rows = await query
            .Where(value => definitionIdentities.Contains(value.DefinitionIdentity))
            .ToArrayAsync(cancellationToken);
        return ConfigurationEffectiveValueMapper.ToEntityDictionary(rows);
    }

    private static void ApplyMutation(
        ConfigurationDbContext dbContext,
        IDictionary<string, ConfigurationEffectiveValueEntity> entities,
        ConfigurationMutationBatchCommitItem item)
    {
        var save = item.SaveRequest;
        var definitionKey = save.Definition.DefinitionKey;
        entities.TryGetValue(definitionKey, out var entity);
        var currentVersion = entity?.Version ?? 0;
        if (save.ExpectedVersion is not null && currentVersion != save.ExpectedVersion)
        {
            throw new ConfigurationConcurrencyConflictException(
                $"Expected version {save.ExpectedVersion} for '{definitionKey}', but current version is {currentVersion}.");
        }

        if (entity is null)
        {
            entity = ConfigurationEffectiveValueEntity.Create(definitionKey);
            dbContext.ConfigurationEffectiveValues.Add(entity);
            entities[definitionKey] = entity;
        }

        var nextVersion = entity.Version + 1;
        foreach (var history in item.Histories)
        {
            if (history.Version != nextVersion)
            {
                throw new InvalidOperationException(
                    $"Prepared history version {history.Version} for '{definitionKey}' does not follow store version {entity.Version}.");
            }
        }

        entity.Apply(
            ConfigurationPersistenceValueConverter.NormalizeJson(save.Json),
            save.Definition.SchemaVersion,
            item.Histories[^1].ModifiedTime.UtcDateTime,
            save.Context.ModifierId,
            save.Context.ModifierName);
        dbContext.ConfigurationValueHistories.AddRange(item.Histories.Select(ConfigurationHistoryMapper.ToEntity));
    }

    private static ConfigurationMutationBatchCommitResult CreateResult(
        ConfigurationMutationBatchCommitRequest request,
        ConfigurationMutationGroup group,
        IReadOnlyDictionary<string, ConfigurationEffectiveValueEntity> entities)
    {
        return new ConfigurationMutationBatchCommitResult
        {
            MutationGroup = group,
            AppliedRequestIds = request.Items.SelectMany(static item => item.RequestIds).ToArray(),
            Documents = entities.ToDictionary(
                static pair => pair.Key,
                static pair => ConfigurationEffectiveValueMapper.ToDocument(pair.Value),
                StringComparer.OrdinalIgnoreCase)
        };
    }
}
