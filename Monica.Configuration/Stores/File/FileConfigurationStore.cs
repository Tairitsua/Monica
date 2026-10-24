using System.Text.Json;
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Serialization;
using Monica.Configuration.Utils;
using Microsoft.Extensions.Options;

namespace Monica.Configuration.Stores.File;

/// <summary>
/// File-backed store bundle for monolith and local Monica.Configuration deployments.
/// </summary>
public sealed class FileConfigurationStore(IOptions<ConfigurationFileStoreOptions> options)
    : IConfigurationEffectiveValueStore,
        IConfigurationHistoryStore,
        IConfigurationMetadataStore,
        IConfigurationUnifiedVersionStore,
        IConfigurationDefinitionMaintenanceStore
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new()
    {
        Encoder = ConfigurationPersistedJsonOptions.ReadableValue.Encoder,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JSON_LINE_OPTIONS = ConfigurationPersistedJsonOptions.CompactValue;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConfigurationFileStoreOptions _options = options.Value;
    private bool _layoutValidated;

    /// <inheritdoc />
    public bool SupportsPublishedDefinitionGuard => true;

    /// <inheritdoc />
    public ConfigurationStoreDescriptor Descriptor { get; } = new()
    {
        StoreKey = "file:default",
        DisplayName = "File",
        Kind = ConfigurationStoreKind.File,
        SupportsEffectiveValues = true,
        SupportsHistory = true,
        SupportsMetadata = true
    };

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

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var documentsByKey = new Dictionary<string, ConfigurationEffectiveValueDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var seed in seeds)
            {
                if (documentsByKey.ContainsKey(seed.Definition.DefinitionKey))
                {
                    continue;
                }

                var path = GetEffectiveValuePath(seed.Definition.DefinitionKey);
                if (IoFile.Exists(path))
                {
                    documentsByKey[seed.Definition.DefinitionKey] =
                        await ReadDocumentAsync(seed.Definition.DefinitionKey, cancellationToken)
                        ?? throw new InvalidOperationException($"Configuration document '{path}' could not be read.");
                    continue;
                }

                var document = new ConfigurationEffectiveValueDocument
                {
                    DefinitionKey = seed.Definition.DefinitionKey,
                    Json = FormatJson(seed.MaterializeSeedJson()),
                    Version = 1,
                    SchemaVersion = seed.Definition.SchemaVersion,
                    LastModifiedTime = DateTimeOffset.UtcNow
                };

                await WriteDocumentAsync(document, cancellationToken);
                documentsByKey[seed.Definition.DefinitionKey] = document;
            }

            return seeds
                .Select(seed => documentsByKey[seed.Definition.DefinitionKey])
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationEffectiveValueDocument?> GetAsync(string definitionKey, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            return await ReadDocumentAsync(definitionKey, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
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

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var documents = new ConfigurationEffectiveValueDocument?[definitionKeys.Count];
            for (var index = 0; index < definitionKeys.Count; index++)
            {
                documents[index] = await ReadDocumentAsync(definitionKeys[index], cancellationToken);
            }

            return documents;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationEffectiveValueDocument> SaveAsync(
        ConfigurationEffectiveValueSaveRequest request,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, request.PublishedDefinitionExpectation is not null, cancellationToken);
            var existing = await ReadDocumentAsync(request.Definition.DefinitionKey, cancellationToken);
            if (request.PublishedDefinitionExpectation is { } expectation)
            {
                expectation.Validate(await FindPublishedDefinitionEntryAsync(request.Definition.DefinitionKey, cancellationToken), existing);
                if (!ConfigurationPublishedDefinitionExpectation.Matches(request.Definition, expectation.Definition))
                    throw new ConfigurationConcurrencyConflictException("The submitted definition does not match the reviewed publication.");
            }
            var currentVersion = existing?.Version ?? 0;
            if (request.ExpectedVersion is not null && currentVersion != request.ExpectedVersion)
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Expected version {request.ExpectedVersion} for '{request.Definition.DefinitionKey}', but current version is {currentVersion}.");
            }

            EnsureDirectories();
            var now = DateTimeOffset.UtcNow;
            var document = new ConfigurationEffectiveValueDocument
            {
                DefinitionKey = existing?.DefinitionKey ?? request.Definition.DefinitionKey,
                Json = FormatJson(request.Json),
                Version = (existing?.Version ?? 0) + 1,
                SchemaVersion = request.Definition.SchemaVersion,
                LastModifiedTime = now,
                LastModifierId = request.Context.ModifierId,
                LastModifierName = request.Context.ModifierName
            };

            await WriteDocumentAsync(document, cancellationToken);
            return document;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task AppendHistoryAsync(ConfigurationValueHistory history, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var line = JsonSerializer.Serialize(HistoryDto.FromHistory(history), JSON_LINE_OPTIONS);
            await IoFile.AppendAllTextAsync(GetHistoryPath(), line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationValueHistory>> QueryHistoryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? definitionKey,
        LogicalPath? logicalPath,
        string? mutationGroupId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var path = GetHistoryPath();
            if (!IoFile.Exists(path))
            {
                return [];
            }

            var result = new List<ConfigurationValueHistory>();
            await foreach (var line in IoFile.ReadLinesAsync(path, cancellationToken))
            {
                var history = DeserializeHistory(line);
                if (history is null || !MatchesHistoryFilters(
                        history,
                        from,
                        to,
                        definitionKey,
                        logicalPath,
                        mutationGroupId,
                        targetKind: null))
                {
                    continue;
                }

                result.Add(history);
            }

            return SortHistory(result);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationHistoryPageResult> QueryHistoryPageAsync(
        ConfigurationHistoryPageRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var path = GetHistoryPath();
            if (!IoFile.Exists(path))
            {
                return new ConfigurationHistoryPageResult
                {
                    Items = [],
                    NextCursor = null,
                    HasMore = false
                };
            }

            var rowsByMutationUnit = new Dictionary<HistoryPageUnitKey, List<ConfigurationValueHistory>>();
            await foreach (var line in IoFile.ReadLinesAsync(path, cancellationToken))
            {
                var history = DeserializeHistory(line);
                if (history is null || !MatchesHistoryFilters(
                        history,
                        request.From,
                        request.To,
                        request.DefinitionKey,
                        request.LogicalPath,
                        request.MutationGroupId,
                        request.TargetKind))
                {
                    continue;
                }

                var unitKey = history.MutationGroupId is null
                    ? new HistoryPageUnitKey(ConfigurationHistoryUnitKind.StandaloneHistory, history.HistoryId)
                    : new HistoryPageUnitKey(ConfigurationHistoryUnitKind.MutationGroup, history.MutationGroupId);
                if (!rowsByMutationUnit.TryGetValue(unitKey, out var rows))
                {
                    rows = [];
                    rowsByMutationUnit.Add(unitKey, rows);
                }

                rows.Add(history);
            }

            var cursor = request.Cursor;
            var candidates = rowsByMutationUnit
                .Select(static pair => new HistoryPageUnit(pair.Key.UnitKind, pair.Key.UnitId, pair.Value))
                .Where(unit => cursor is null || IsAfterCursor(unit, cursor))
                .OrderByDescending(static unit => unit.ModifiedTime)
                .ThenByDescending(static unit => unit.Version)
                .ThenByDescending(static unit => unit.UnitKind)
                .ThenByDescending(static unit => unit.UnitId, StringComparer.Ordinal)
                .Take(request.PageSize + 1)
                .ToArray();
            var selectedUnits = candidates.Take(request.PageSize).ToArray();
            var hasMore = candidates.Length > request.PageSize;
            var lastUnit = selectedUnits.LastOrDefault();
            return new ConfigurationHistoryPageResult
            {
                Items = SortHistory(selectedUnits.SelectMany(static unit => unit.Rows)),
                NextCursor = hasMore && lastUnit is not null
                    ? new ConfigurationHistoryCursor
                    {
                        ModifiedTime = lastUnit.ModifiedTime,
                        Version = lastUnit.Version,
                        UnitKind = lastUnit.UnitKind,
                        UnitId = lastUnit.UnitId
                    }
                    : null,
                HasMore = hasMore
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationValueHistory?> GetHistoryByIdAsync(string historyId, CancellationToken cancellationToken)
    {
        return (await GetHistoriesByIdsAsync([historyId], cancellationToken)).SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationValueHistory>> GetHistoriesByIdsAsync(
        IReadOnlyCollection<string> historyIds,
        CancellationToken cancellationToken)
    {
        var remainingIds = historyIds
            .Where(static historyId => !string.IsNullOrWhiteSpace(historyId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remainingIds.Count == 0)
        {
            return [];
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var path = GetHistoryPath();
            if (!IoFile.Exists(path))
            {
                return [];
            }

            var histories = new List<ConfigurationValueHistory>(remainingIds.Count);
            await foreach (var line in IoFile.ReadLinesAsync(path, cancellationToken))
            {
                var history = DeserializeHistory(line);
                if (history is null || !remainingIds.Remove(history.HistoryId))
                {
                    continue;
                }

                histories.Add(history);
                if (remainingIds.Count == 0)
                {
                    break;
                }
            }

            return SortHistory(histories);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UpsertGroupAsync(ConfigurationMutationGroup group, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var groups = await ReadGroupsAsync(cancellationToken);
            var index = groups.FindIndex(candidate => string.Equals(candidate.GroupId, group.GroupId, StringComparison.OrdinalIgnoreCase));
            var dto = GroupDto.FromGroup(group);
            if (index >= 0)
            {
                groups[index] = dto;
            }
            else
            {
                groups.Add(dto);
            }

            await IoFile.WriteAllTextAsync(GetGroupsPath(), JsonSerializer.Serialize(groups, JSON_OPTIONS), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationMutationGroup>> ListGroupsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? definitionKey,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            return (await ReadGroupsAsync(cancellationToken))
                .Select(group => group.ToGroup())
                .Where(group =>
                    (from is null || group.CreatedTime >= from)
                    && (to is null || group.CreatedTime <= to)
                    && (string.IsNullOrWhiteSpace(definitionKey) || group.DefinitionKeys.Contains(definitionKey, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(group => group.CreatedTime)
                .ThenByDescending(group => group.GroupId, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationMutationGroupPageResult> QueryGroupsPageAsync(
        ConfigurationMutationGroupPageRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var cursor = request.Cursor;
            var candidates = (await ReadGroupsAsync(cancellationToken))
                .Select(static group => group.ToGroup())
                .Where(group =>
                    (request.From is null || group.CreatedTime >= request.From)
                    && (request.To is null || group.CreatedTime <= request.To)
                    && (string.IsNullOrWhiteSpace(request.DefinitionKey)
                        || group.DefinitionKeys.Contains(request.DefinitionKey, StringComparer.OrdinalIgnoreCase)))
                .Where(group => cursor is null || IsAfterCursor(group, cursor))
                .OrderByDescending(static group => group.CreatedTime)
                .ThenByDescending(static group => group.GroupId, StringComparer.Ordinal)
                .Take(request.PageSize + 1)
                .ToArray();
            var selectedGroups = candidates.Take(request.PageSize).ToArray();
            var hasMore = candidates.Length > request.PageSize;
            var lastGroup = selectedGroups.LastOrDefault();
            return new ConfigurationMutationGroupPageResult
            {
                Items = selectedGroups,
                NextCursor = hasMore && lastGroup is not null
                    ? new ConfigurationMutationGroupCursor
                    {
                        CreatedTime = lastGroup.CreatedTime,
                        GroupId = lastGroup.GroupId
                    }
                    : null,
                HasMore = hasMore
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationMutationGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            return (await ReadGroupsAsync(cancellationToken))
                .Select(group => group.ToGroup())
                .FirstOrDefault(group => string.Equals(group.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationUnifiedVersionSnapshot> AppendVersionAsync(
        ConfigurationUnifiedVersionCreateRequest request,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var duplicateDefinition = request.Definitions
                .GroupBy(static definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Skip(1).Any());
            if (duplicateDefinition is not null)
            {
                throw new InvalidOperationException(
                    $"Unified version snapshots cannot contain duplicate definition key '{duplicateDefinition.Key}' ignoring casing.");
            }

            var definitions = request.Definitions.ToArray();
            var summaries = await ReadUnifiedVersionIndexAsync(cancellationToken);
            RecoverUnifiedVersionDeletions(summaries);
            var version = summaries.Count == 0 ? 1 : summaries.Max(static summary => summary.Version) + 1;
            var summary = new ConfigurationUnifiedVersionSummary
            {
                Version = version,
                MutationGroupId = request.MutationGroupId,
                TriggerDefinitionKeys = NormalizeKeys(request.TriggerDefinitionKeys),
                DefinitionKeys = NormalizeKeys(definitions.Select(static definition => definition.DefinitionKey)),
                DefinitionCount = definitions.Length,
                CreatedTime = request.CreatedTime,
                ModifierId = request.ModifierId,
                ModifierName = request.ModifierName,
                Reason = request.Reason
            };
            var snapshot = new ConfigurationUnifiedVersionSnapshot
            {
                Summary = summary,
                Definitions = definitions
            };

            await IoFile.WriteAllTextAsync(
                GetUnifiedVersionSnapshotPath(version),
                JsonSerializer.Serialize(snapshot, JSON_OPTIONS),
                cancellationToken);
            summaries.Add(summary);
            summaries = summaries
                .OrderByDescending(static item => item.Version)
                .ToList();
            await WriteUnifiedVersionIndexAsync(summaries, cancellationToken);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationUnifiedVersionSummary>> ListVersionsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? definitionKey,
        int limit,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var normalizedLimit = Math.Clamp(limit, 1, 500);
            return (await ReadUnifiedVersionIndexAsync(cancellationToken))
                .Where(summary =>
                    (from is null || summary.CreatedTime >= from)
                    && (to is null || summary.CreatedTime <= to)
                    && (string.IsNullOrWhiteSpace(definitionKey)
                        || summary.DefinitionKeys.Contains(definitionKey, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(static summary => summary.Version)
                .Take(normalizedLimit)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationUnifiedVersionSnapshot?> GetVersionAsync(
        long version,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var summaries = await ReadUnifiedVersionIndexAsync(cancellationToken);
            if (summaries.All(summary => summary.Version != version))
            {
                return null;
            }

            var path = GetUnifiedVersionSnapshotPath(version);
            return IoFile.Exists(path)
                ? JsonSerializer.Deserialize<ConfigurationUnifiedVersionSnapshot>(
                    await IoFile.ReadAllTextAsync(path, cancellationToken),
                    JSON_OPTIONS)
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationUnifiedVersionSnapshot?> GetVersionByMutationGroupAsync(
        string mutationGroupId,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var summary = (await ReadUnifiedVersionIndexAsync(cancellationToken))
                .FirstOrDefault(candidate => string.Equals(candidate.MutationGroupId, mutationGroupId, StringComparison.OrdinalIgnoreCase));
            if (summary is null)
            {
                return null;
            }

            var path = GetUnifiedVersionSnapshotPath(summary.Version);
            return IoFile.Exists(path)
                ? JsonSerializer.Deserialize<ConfigurationUnifiedVersionSnapshot>(
                    await IoFile.ReadAllTextAsync(path, cancellationToken),
                    JSON_OPTIONS)
                : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteVersionAsync(long version, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var summaries = await ReadUnifiedVersionIndexAsync(cancellationToken);
            RecoverUnifiedVersionDeletions(summaries);
            var summaryIndex = summaries.FindIndex(candidate => candidate.Version == version);
            if (summaryIndex < 0)
            {
                throw new KeyNotFoundException($"Unified configuration version '{version}' was not found.");
            }

            var latestVersion = summaries.Max(static candidate => candidate.Version);
            if (version == latestVersion)
            {
                throw new InvalidOperationException(
                    $"Unified configuration version '{version}' is the current version and cannot be deleted.");
            }

            var deletionMarkerPath = GetUnifiedVersionDeletionMarkerPath(version);
            await IoFile.WriteAllTextAsync(deletionMarkerPath, string.Empty, cancellationToken);
            try
            {
                summaries.RemoveAt(summaryIndex);
                await WriteUnifiedVersionIndexAsync(summaries, cancellationToken);
            }
            catch
            {
                _ = TryDeleteFile(deletionMarkerPath);
                throw;
            }

            // The index replacement is the commit point. Cleanup is recoverable and must not turn a committed delete
            // into a reported failure when the snapshot cannot be removed immediately.
            FinalizeUnifiedVersionDeletion(version, deletionMarkerPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task PublishAsync(ConfigurationDefinitionPublicationBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        // The file store is a local/monolith provider, so its canonical value is the aggregate of this one publisher.
        var publications = batch.Publications
            .Select(static publication => new
            {
                Publication = publication,
                Definition = publication.Definition with
                {
                    ReloadBehavior = ConfigurationReloadBehaviorObservation.Aggregate(
                        [publication.ReloadBehaviorObservation])
                }
            })
            .ToArray();

        var duplicateDefinition = publications
            .GroupBy(
                static publication => ConfigurationDefinitionIdentity.Compute(publication.Definition.DefinitionKey),
                StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Skip(1).Any());
        if (duplicateDefinition is not null)
        {
            var keys = duplicateDefinition
                .Select(static publication => $"'{publication.Definition.DefinitionKey}'")
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToArray();
            throw new ConfigurationValidationFailedException(
                $"The publisher supplied multiple definitions with the same case-insensitive identity: {string.Join(", ", keys)}.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var definitionPaths = EnumerateDefinitionPaths(cancellationToken);
            var incomingIdentities = publications
                .Select(static publication =>
                    ConfigurationDefinitionIdentity.Compute(publication.Definition.DefinitionKey))
                .Concat(batch.ExcludedDefinitionKeys.Select(ConfigurationDefinitionIdentity.Compute))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var publication in publications)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var definition = publication.Definition;
                var path = GetDefinitionPath(definition.DefinitionKey);
                var existing = IoFile.Exists(path)
                    ? await TryReadPublishedDefinitionDtoForRepairAsync(path, cancellationToken)
                    : null;
                if (existing is not null)
                {
                    ValidatePersistedIdentity(path, existing.DefinitionKey, "definition metadata");
                }

                var published = PublishedDefinitionDto.FromPublication(
                    definition,
                    batch.Publisher.PublisherKey,
                    publication.Publication.ReloadBehaviorObservation,
                    existing);
                if (existing is not null && published.Matches(existing))
                {
                    continue;
                }

                await IoFile.WriteAllTextAsync(path, JsonSerializer.Serialize(published, JSON_OPTIONS), cancellationToken);
            }

            await WithdrawMissingPublisherStatesAsync(
                definitionPaths,
                batch.Publisher.PublisherKey,
                incomingIdentities,
                cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RetirePublisherAsync(
        ConfigurationPublisherIdentity publisher,
        CancellationToken cancellationToken)
    {
        var retirement = ConfigurationDefinitionPublicationBatch.Create(publisher, []);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            await WithdrawMissingPublisherStatesAsync(
                EnumerateDefinitionPaths(cancellationToken),
                retirement.Publisher.PublisherKey,
                new HashSet<string>(StringComparer.Ordinal),
                cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationDefinitionPublicationOverview> GetDefinitionPublicationOverviewAsync(
        string definitionKey,
        int limit,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var path = GetDefinitionPath(definitionKey);
            if (!IoFile.Exists(path))
            {
                throw new KeyNotFoundException(
                    $"Published configuration definition '{definitionKey}' was not found.");
            }

            var dto = await TryReadPublishedDefinitionDtoForRepairAsync(path, cancellationToken)
                      ?? throw new InvalidDataException(
                          $"Published configuration definition '{definitionKey}' could not be read.");
            ValidatePersistedIdentity(path, dto.DefinitionKey, "definition metadata");
            if (!Enum.TryParse<ConfigurationReloadBehavior>(
                    dto.ReloadBehavior,
                    ignoreCase: false,
                    out var reloadBehavior)
                || !Enum.IsDefined(reloadBehavior))
            {
                throw new InvalidDataException(
                    $"Published definition '{definitionKey}' uses unsupported reload behavior '{dto.ReloadBehavior}'.");
            }

            return new ConfigurationDefinitionPublicationOverview
            {
                DefinitionKey = dto.DefinitionKey,
                LifecycleState = dto.PublisherState is null
                    ? ConfigurationDefinitionLifecycleState.Retired
                    : ConfigurationDefinitionLifecycleState.Active,
                DefinitionRevision = dto.DefinitionRevision,
                SchemaVersion = dto.SchemaVersion,
                ReloadBehavior = reloadBehavior,
                PublisherStates = dto.PublisherState is null
                    ? []
                    : [dto.PublisherState.ToModel()],
                RevisionHistories = []
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>>
        GetDefinitionPublisherStatesAsync(
            IReadOnlyCollection<string> definitionKeys,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitionKeys);
        var normalizedDefinitionKeys = definitionKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim())
            .GroupBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static key => key, StringComparer.Ordinal).First())
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static key => key, StringComparer.Ordinal)
            .ToArray();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var result = new Dictionary<string, IReadOnlyList<ConfigurationDefinitionPublisherState>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var definitionKey in normalizedDefinitionKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = GetDefinitionPath(definitionKey);
                if (!IoFile.Exists(path))
                {
                    result[definitionKey] = [];
                    continue;
                }

                var dto = await TryReadPublishedDefinitionDtoForRepairAsync(path, cancellationToken)
                          ?? throw new InvalidDataException(
                              $"Published configuration definition '{definitionKey}' could not be read.");
                ValidatePersistedIdentity(path, dto.DefinitionKey, "definition metadata");
                result[definitionKey] = dto.PublisherState is null
                    ? []
                    : [dto.PublisherState.ToModel()];
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationDefinitionPurgePreview> PreviewDefinitionPurgeAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            var (_, storedDefinitionKey, definition) =
                await LoadDefinitionForMaintenanceAsync(definitionKey, cancellationToken);
            var effectiveValue = await ReadDocumentAsync(storedDefinitionKey, cancellationToken);
            var (retainedValueHistoryCount, retainedMutationGroupCount) =
                await CountRetainedHistoryAsync(storedDefinitionKey, cancellationToken);
            var retainedUnifiedVersionCount = (await ReadUnifiedVersionIndexAsync(cancellationToken))
                .Count(summary => summary.DefinitionKeys.Contains(
                    storedDefinitionKey,
                    StringComparer.OrdinalIgnoreCase));
            IReadOnlyList<string> activePublisherKeys = definition.PublisherState is null
                ? []
                : new[] { definition.PublisherState.PublisherKey };

            return new ConfigurationDefinitionPurgePreview
            {
                DefinitionKey = storedDefinitionKey,
                DisplayName = definition.DisplayName,
                LifecycleState = definition.PublisherState is null
                    ? ConfigurationDefinitionLifecycleState.Retired
                    : ConfigurationDefinitionLifecycleState.Active,
                DefinitionRevision = definition.DefinitionRevision,
                ActivePublisherKeys = activePublisherKeys,
                IsLocallyRegistered = false,
                HasEffectiveValue = effectiveValue is not null,
                EffectiveValueVersion = effectiveValue?.Version,
                PublicationHistoryCount = 0,
                RetainedValueHistoryCount = retainedValueHistoryCount,
                RetainedMutationGroupCount = retainedMutationGroupCount,
                RetainedUnifiedVersionCount = retainedUnifiedVersionCount
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task PurgeDefinitionAsync(
        ConfigurationDefinitionPurgeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            await using var writeLease = await ConfigurationFileWriteLease.AcquireAsync(
                _options.RootDirectory, false, cancellationToken);
            EnsureDirectories();
            var (definitionPath, storedDefinitionKey, definition) = await LoadDefinitionForMaintenanceAsync(
                request.DefinitionKey,
                cancellationToken);
            if (definition.PublisherState is not null)
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Configuration definition '{definition.DefinitionKey}' is active and cannot be purged. "
                    + $"Current publisher: {definition.PublisherState.PublisherKey}.");
            }

            if (definition.DefinitionRevision != request.ExpectedDefinitionRevision)
            {
                throw new ConfigurationConcurrencyConflictException(
                    $"Expected definition revision {request.ExpectedDefinitionRevision} for "
                    + $"'{definition.DefinitionKey}', but current revision is {definition.DefinitionRevision}.");
            }

            var effectiveValuePath = GetEffectiveValuePath(storedDefinitionKey);
            var effectiveMetadataPath = GetMetadataPath(storedDefinitionKey);

            // The definition file is the maintenance record's commit point. A crash while removing value files leaves
            // a retryable retired definition; removing the definition last never hides a partially completed purge.
            if (IoFile.Exists(effectiveValuePath))
            {
                IoFile.Delete(effectiveValuePath);
            }

            if (IoFile.Exists(effectiveMetadataPath))
            {
                IoFile.Delete(effectiveMetadataPath);
            }

            IoFile.Delete(definitionPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConfigurationPublishedDefinitionEntry>> ListPublishedDefinitionEntriesAsync(
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            return await ReadPublishedDefinitionEntriesAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConfigurationPublishedDefinitionEntry?> GetPublishedDefinitionEntryAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            ValidateStorageLayout(cancellationToken);
            return await FindPublishedDefinitionEntryAsync(definitionKey, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ConfigurationEffectiveValueDocument?> ReadDocumentAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        var path = GetEffectiveValuePath(definitionKey);
        if (!IoFile.Exists(path))
        {
            return null;
        }

        var metadata = await ReadMetadataAsync(definitionKey, cancellationToken);
        var storedDefinitionKey = metadata?.DefinitionKey ?? definitionKey;
        var json = await IoFile.ReadAllTextAsync(path, cancellationToken);
        var lastWrite = IoFile.GetLastWriteTimeUtc(path);
        return new ConfigurationEffectiveValueDocument
        {
            DefinitionKey = storedDefinitionKey,
            Json = FormatJson(json),
            Version = metadata?.Version ?? 1,
            SchemaVersion = metadata?.SchemaVersion ?? 1,
            LastModifiedTime = metadata?.LastModifiedTime ?? new DateTimeOffset(lastWrite, TimeSpan.Zero),
            LastModifierId = metadata?.LastModifierId,
            LastModifierName = metadata?.LastModifierName
        };
    }

    private async Task<(string Path, string StoredDefinitionKey, PublishedDefinitionDto Definition)> LoadDefinitionForMaintenanceAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        var definitionPath = GetDefinitionPath(definitionKey);
        if (!IoFile.Exists(definitionPath))
        {
            throw new KeyNotFoundException(
                $"Published configuration definition '{definitionKey}' was not found.");
        }

        var definition = await TryReadPublishedDefinitionDtoForRepairAsync(definitionPath, cancellationToken)
                         ?? throw new InvalidDataException(
                             $"Published configuration definition '{definitionKey}' could not be read.");
        ValidatePersistedIdentity(definitionPath, definition.DefinitionKey, "definition metadata");

        return (definitionPath, definition.DefinitionKey, definition);
    }

    private async Task<(int ValueHistoryCount, int MutationGroupCount)> CountRetainedHistoryAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        var historyPath = GetHistoryPath();
        if (!IoFile.Exists(historyPath))
        {
            return (0, 0);
        }

        var valueHistoryCount = 0;
        var mutationGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var line in IoFile.ReadLinesAsync(historyPath, cancellationToken))
        {
            var history = DeserializeHistory(line);
            if (history is null
                || !string.Equals(history.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            valueHistoryCount++;
            if (!string.IsNullOrWhiteSpace(history.MutationGroupId))
            {
                mutationGroupIds.Add(history.MutationGroupId);
            }
        }

        return (valueHistoryCount, mutationGroupIds.Count);
    }

    private async Task WriteDocumentAsync(ConfigurationEffectiveValueDocument document, CancellationToken cancellationToken)
    {
        var effectiveValuePath = GetEffectiveValuePath(document.DefinitionKey);
        await IoFile.WriteAllTextAsync(effectiveValuePath, document.Json, cancellationToken);
        var metadata = new DocumentMetadataDto
        {
            DefinitionKey = document.DefinitionKey,
            Version = document.Version,
            SchemaVersion = document.SchemaVersion,
            LastModifiedTime = document.LastModifiedTime,
            LastModifierId = document.LastModifierId,
            LastModifierName = document.LastModifierName
        };
        var metadataPath = GetMetadataPath(document.DefinitionKey);
        await IoFile.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JSON_OPTIONS), cancellationToken);
    }

    private async Task<DocumentMetadataDto?> ReadMetadataAsync(string definitionKey, CancellationToken cancellationToken)
    {
        var path = GetMetadataPath(definitionKey);
        if (!IoFile.Exists(path))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<DocumentMetadataDto>(
                           await IoFile.ReadAllTextAsync(path, cancellationToken),
                           JSON_OPTIONS)
                       ?? throw new InvalidDataException(
                           $"Persisted effective-value metadata file '{Path.GetFileName(path)}' contains no object.");
        ValidatePersistedIdentity(path, metadata.DefinitionKey, "effective-value metadata", ".metadata.json");
        return metadata;
    }

    private async Task<List<GroupDto>> ReadGroupsAsync(CancellationToken cancellationToken)
    {
        var path = GetGroupsPath();
        if (!IoFile.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<GroupDto>>(await IoFile.ReadAllTextAsync(path, cancellationToken)) ?? [];
    }

    private async Task<List<ConfigurationUnifiedVersionSummary>> ReadUnifiedVersionIndexAsync(
        CancellationToken cancellationToken)
    {
        var path = GetUnifiedVersionIndexPath();
        if (!IoFile.Exists(path))
        {
            return [];
        }

        var summaries = JsonSerializer.Deserialize<List<ConfigurationUnifiedVersionSummary>>(
            await IoFile.ReadAllTextAsync(path, cancellationToken),
            JSON_OPTIONS) ?? [];
        return summaries;
    }

    private void RecoverUnifiedVersionDeletions(
        IReadOnlyList<ConfigurationUnifiedVersionSummary> summaries)
    {
        var unifiedVersionDirectory = GetUnifiedVersionDirectory();
        if (!IoDirectory.Exists(unifiedVersionDirectory))
        {
            return;
        }

        var retainedVersions = summaries
            .Select(static summary => summary.Version)
            .ToHashSet();
        foreach (var markerPath in IoDirectory.EnumerateFiles(unifiedVersionDirectory, "v*.deleting"))
        {
            var markerName = Path.GetFileNameWithoutExtension(markerPath);
            if (markerName.Length <= 1 || markerName[0] != 'v'
                                       || !long.TryParse(markerName.AsSpan(1), out var version))
            {
                continue;
            }

            if (retainedVersions.Contains(version))
            {
                // The index update never committed, so discard only the abandoned intent marker.
                _ = TryDeleteFile(markerPath);
                continue;
            }

            FinalizeUnifiedVersionDeletion(version, markerPath);
        }
    }

    private void FinalizeUnifiedVersionDeletion(long version, string deletionMarkerPath)
    {
        if (TryDeleteFile(GetUnifiedVersionSnapshotPath(version)))
        {
            _ = TryDeleteFile(deletionMarkerPath);
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            IoFile.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task WriteUnifiedVersionIndexAsync(
        IReadOnlyList<ConfigurationUnifiedVersionSummary> summaries,
        CancellationToken cancellationToken)
    {
        var indexPath = GetUnifiedVersionIndexPath();
        var stagingPath = $"{indexPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            // Replace the complete index in one filesystem operation so readers never observe a partial JSON document.
            await IoFile.WriteAllTextAsync(
                stagingPath,
                JsonSerializer.Serialize(summaries, JSON_OPTIONS),
                cancellationToken);
            IoFile.Move(stagingPath, indexPath, overwrite: true);
        }
        finally
        {
            if (IoFile.Exists(stagingPath))
            {
                IoFile.Delete(stagingPath);
            }
        }
    }

    private void EnsureDirectories()
    {
        IoDirectory.CreateDirectory(GetEffectiveDirectory());
        IoDirectory.CreateDirectory(GetEffectiveMetadataDirectory());
        IoDirectory.CreateDirectory(GetHistoryDirectory());
        IoDirectory.CreateDirectory(GetUnifiedVersionDirectory());
        IoDirectory.CreateDirectory(GetDefinitionsDirectory());
    }

    private void ValidateStorageLayout(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_layoutValidated)
        {
            return;
        }

        // Validate before creating directories so read-only startup snapshots cannot hide or mutate a legacy store.
        var legacyPath = FindLegacyLayoutPath(
                             GetDefinitionsDirectory(),
                             "*.json",
                             static path => Path.GetFileNameWithoutExtension(path),
                             cancellationToken)
                         ?? FindLegacyLayoutPath(
                             GetEffectiveDirectory(),
                             "*.json",
                             static path => Path.GetFileNameWithoutExtension(path),
                             cancellationToken)
                         ?? FindLegacyLayoutPath(
                             GetEffectiveMetadataDirectory(),
                             "*.metadata.json",
                             static path => Path.GetFileName(path)[..^".metadata.json".Length],
                             cancellationToken);
        if (legacyPath is not null)
        {
            var relativePath = Path.GetRelativePath(_options.RootDirectory, legacyPath);
            throw new InvalidDataException(
                $"The file-backed configuration store contains legacy definition-key filename '{relativePath}'. "
                + "Migrate or recreate the store with identity-addressed filenames before startup; "
                + "Monica does not migrate file-store data automatically.");
        }

        _layoutValidated = true;
    }

    private static string? FindLegacyLayoutPath(
        string directory,
        string searchPattern,
        Func<string, string> getIdentity,
        CancellationToken cancellationToken)
    {
        if (!IoDirectory.Exists(directory))
        {
            return null;
        }

        foreach (var path in IoDirectory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCanonicalIdentity(getIdentity(path)))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsCanonicalIdentity(string value)
    {
        return value.Length == ConfigurationDefinitionIdentity.Length
               && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    private string GetEffectiveDirectory()
    {
        return Path.Combine(_options.RootDirectory, "effective");
    }

    private string GetEffectiveMetadataDirectory()
    {
        return Path.Combine(GetEffectiveDirectory(), ".metadata");
    }

    private string GetHistoryDirectory()
    {
        return Path.Combine(_options.RootDirectory, "history");
    }

    private string GetDefinitionsDirectory()
    {
        return Path.Combine(_options.RootDirectory, "metadata", "definitions");
    }

    private string GetEffectiveValuePath(string definitionKey)
    {
        return Path.Combine(
            GetEffectiveDirectory(),
            $"{ConfigurationDefinitionIdentity.Compute(definitionKey)}.json");
    }

    private string GetMetadataPath(string definitionKey)
    {
        return Path.Combine(
            GetEffectiveMetadataDirectory(),
            $"{ConfigurationDefinitionIdentity.Compute(definitionKey)}.metadata.json");
    }

    private string GetDefinitionPath(string definitionKey)
    {
        return Path.Combine(
            GetDefinitionsDirectory(),
            $"{ConfigurationDefinitionIdentity.Compute(definitionKey)}.json");
    }

    private static void ValidatePersistedIdentity(
        string path,
        string definitionKey,
        string documentKind,
        string suffix = ".json")
    {
        string persistedIdentity;
        try
        {
            persistedIdentity = ConfigurationDefinitionIdentity.Compute(definitionKey);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(
                $"Persisted {documentKind} file '{Path.GetFileName(path)}' has no valid definition key.",
                ex);
        }

        var fileName = Path.GetFileName(path);
        var fileIdentity = fileName.EndsWith(suffix, StringComparison.Ordinal)
            ? fileName[..^suffix.Length]
            : fileName;
        if (!string.Equals(fileIdentity, persistedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted {documentKind} file '{fileName}' claims definition key '{definitionKey}', "
                + $"whose identity is '{persistedIdentity}' instead of filename identity '{fileIdentity}'.");
        }
    }

    private string GetHistoryPath()
    {
        return Path.Combine(GetHistoryDirectory(), "history.jsonl");
    }

    private string GetGroupsPath()
    {
        return Path.Combine(GetHistoryDirectory(), "groups.json");
    }

    private string GetUnifiedVersionDirectory()
    {
        return Path.Combine(GetHistoryDirectory(), "unified-versions");
    }

    private string GetUnifiedVersionIndexPath()
    {
        return Path.Combine(GetUnifiedVersionDirectory(), "index.json");
    }

    private string GetUnifiedVersionSnapshotPath(long version)
    {
        return Path.Combine(GetUnifiedVersionDirectory(), $"v{version}.json");
    }

    private string GetUnifiedVersionDeletionMarkerPath(long version)
    {
        return Path.Combine(GetUnifiedVersionDirectory(), $"v{version}.deleting");
    }

    private static string FormatJson(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return JsonSerializer.Serialize(document.RootElement, JSON_OPTIONS);
    }

    private static IReadOnlyList<string> NormalizeKeys(IEnumerable<string> keys)
    {
        return keys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<ConfigurationPublishedDefinitionEntry>> ReadPublishedDefinitionEntriesAsync(
        CancellationToken cancellationToken)
    {
        var entries = new List<ConfigurationPublishedDefinitionEntry>();
        foreach (var path in EnumerateDefinitionPaths(cancellationToken))
        {
            entries.Add(await ReadPublishedDefinitionEntryAsync(path, cancellationToken));
        }

        return entries
            .OrderBy(entry => DisplayNameOrKey(entry.Metadata), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Metadata.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ConfigurationPublishedDefinitionEntry?> FindPublishedDefinitionEntryAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        var path = GetDefinitionPath(definitionKey);
        if (!IoFile.Exists(path))
        {
            return null;
        }

        return await ReadPublishedDefinitionEntryAsync(path, cancellationToken);
    }

    private IReadOnlyList<string> EnumerateDefinitionPaths(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        if (!IoDirectory.Exists(GetDefinitionsDirectory())) return paths;
        foreach (var path in IoDirectory.EnumerateFiles(GetDefinitionsDirectory(), "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(path);
        }

        return paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
    }

    private static async Task WithdrawMissingPublisherStatesAsync(
        IReadOnlyList<string> definitionPaths,
        string publisherKey,
        IReadOnlySet<string> incomingIdentities,
        CancellationToken cancellationToken)
    {
        foreach (var path in definitionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var persistedIdentity = Path.GetFileNameWithoutExtension(path);
            if (incomingIdentities.Contains(persistedIdentity))
            {
                continue;
            }

            var existing = await TryReadPublishedDefinitionDtoForRepairAsync(path, cancellationToken);
            if (existing is not null)
            {
                ValidatePersistedIdentity(path, existing.DefinitionKey, "definition metadata");
            }

            var withdrawn = existing?.WithdrawPublisher(publisherKey);
            if (existing is null || withdrawn is null || withdrawn.Matches(existing))
            {
                continue;
            }

            await IoFile.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(withdrawn, JSON_OPTIONS),
                cancellationToken);
        }
    }

    private async Task<ConfigurationPublishedDefinitionEntry> ReadPublishedDefinitionEntryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var content = await IoFile.ReadAllTextAsync(path, cancellationToken);
        try
        {
            var dto = DeserializePublishedDefinitionDto(content);
            if (dto is not null)
            {
                var record = dto.ToRecord(Descriptor.StoreKey);
                try
                {
                    ValidatePersistedIdentity(path, record.DefinitionKey, "definition metadata");
                }
                catch (InvalidDataException ex)
                {
                    return ConfigurationPublishedDefinitionEntry.FromEnvelopeFailure(
                        record,
                        ex,
                        ConfigurationDefinitionMetadataRepairAction.RepairMetadataStore);
                }

                return ConfigurationPublishedDefinitionEntry.Materialize(record);
            }

            return ConfigurationPublishedDefinitionEntry.FromEnvelopeFailure(
                CreateFallbackPublishedDefinitionRecord(path),
                new JsonException("Persisted definition metadata JSON contains no object."));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return ConfigurationPublishedDefinitionEntry.FromEnvelopeFailure(
                CreateFallbackPublishedDefinitionRecord(path, TryReadPartialPublishedDefinitionDto(content)),
                ex);
        }
    }

    private static async Task<PublishedDefinitionDto?> TryReadPublishedDefinitionDtoForRepairAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var content = await IoFile.ReadAllTextAsync(path, cancellationToken);
        try
        {
            return DeserializePublishedDefinitionDto(content);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return TryReadPartialPublishedDefinitionDto(content);
        }
    }

    private static PublishedDefinitionDto? DeserializePublishedDefinitionDto(string content)
    {
        return JsonSerializer.Deserialize<PublishedDefinitionDto>(content, JSON_OPTIONS);
    }

    private static PublishedDefinitionDto? TryReadPartialPublishedDefinitionDto(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var root = document.RootElement;
            return new PublishedDefinitionDto
            {
                DefinitionKey = ReadString(root, nameof(PublishedDefinitionDto.DefinitionKey)),
                SectionPath = ReadString(root, nameof(PublishedDefinitionDto.SectionPath)),
                DisplayName = ReadString(root, nameof(PublishedDefinitionDto.DisplayName)),
                Description = ReadOptionalString(root, nameof(PublishedDefinitionDto.Description)),
                ClrTypeName = ReadString(root, nameof(PublishedDefinitionDto.ClrTypeName)),
                FromProject = ReadString(root, nameof(PublishedDefinitionDto.FromProject)),
                Category = ReadOptionalString(root, nameof(PublishedDefinitionDto.Category)),
                SchemaVersion = ReadInt32(root, nameof(PublishedDefinitionDto.SchemaVersion)),
                DefinitionRevision = ReadInt32(root, nameof(PublishedDefinitionDto.DefinitionRevision)),
                SchemaHash = ReadString(root, nameof(PublishedDefinitionDto.SchemaHash)),
                ReloadBehavior = ReadString(root, nameof(PublishedDefinitionDto.ReloadBehavior)),
                SchemaJson = ReadString(root, nameof(PublishedDefinitionDto.SchemaJson)),
                PublisherState = root.TryGetProperty(
                                     nameof(PublishedDefinitionDto.PublisherState),
                                     out var publisherState)
                                 && publisherState.ValueKind == JsonValueKind.Object
                    ? publisherState.Deserialize<PublishedDefinitionPublisherStateDto>(JSON_OPTIONS)
                    : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return ReadOptionalString(element, propertyName) ?? string.Empty;
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private ConfigurationPublishedDefinitionRecord CreateFallbackPublishedDefinitionRecord(
        string path,
        PublishedDefinitionDto? partial = null)
    {
        var persistedIdentity = Path.GetFileNameWithoutExtension(path);
        if (partial is not null)
        {
            var metadata = partial.ToRecord(Descriptor.StoreKey);
            var definitionKey = string.IsNullOrWhiteSpace(metadata.DefinitionKey)
                ? persistedIdentity
                : metadata.DefinitionKey;
            return metadata with
            {
                DefinitionKey = definitionKey,
                DisplayName = string.IsNullOrWhiteSpace(metadata.DisplayName)
                    ? definitionKey
                    : metadata.DisplayName
            };
        }

        var fallbackDefinitionKey = persistedIdentity;
        return new ConfigurationPublishedDefinitionRecord
        {
            StoreKey = Descriptor.StoreKey,
            DefinitionKey = fallbackDefinitionKey,
            LifecycleState = ConfigurationDefinitionLifecycleState.Retired,
            SectionPath = "",
            DisplayName = fallbackDefinitionKey,
            ClrTypeName = "",
            FromProject = "",
            SchemaVersion = 0,
            DefinitionRevision = 0,
            SchemaHash = "",
            ReloadBehavior = "",
            SchemaJson = ""
        };
    }

    private static string DisplayNameOrKey(ConfigurationPublishedDefinitionRecord metadata)
    {
        return string.IsNullOrWhiteSpace(metadata.DisplayName) ? metadata.DefinitionKey : metadata.DisplayName;
    }

    private static IReadOnlyList<ConfigurationValueHistory> SortHistory(IEnumerable<ConfigurationValueHistory> histories)
    {
        return histories
            .OrderByDescending(history => history.ModifiedTime)
            .ThenByDescending(history => history.Version)
            .ThenByDescending(history => history.HistoryId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool MatchesHistoryFilters(
        ConfigurationValueHistory history,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? definitionKey,
        LogicalPath? logicalPath,
        string? mutationGroupId,
        ConfigurationMutationTargetKind? targetKind)
    {
        return (from is null || history.ModifiedTime >= from)
               && (to is null || history.ModifiedTime <= to)
               && (string.IsNullOrWhiteSpace(definitionKey)
                   || string.Equals(history.DefinitionKey, definitionKey, StringComparison.OrdinalIgnoreCase))
               && (logicalPath is null
                   || history.LogicalPath.ToCanonicalString() == logicalPath.ToCanonicalString())
               && (string.IsNullOrWhiteSpace(mutationGroupId)
                   || string.Equals(history.MutationGroupId, mutationGroupId, StringComparison.OrdinalIgnoreCase))
               && (targetKind is null || history.TargetKind == targetKind.Value);
    }

    private static ConfigurationValueHistory? DeserializeHistory(string line)
    {
        return string.IsNullOrWhiteSpace(line)
            ? null
            : JsonSerializer.Deserialize<HistoryDto>(line)?.ToHistory();
    }

    private static bool IsAfterCursor(
        HistoryPageUnit unit,
        ConfigurationHistoryCursor cursor)
    {
        var timeComparison = unit.ModifiedTime.CompareTo(cursor.ModifiedTime);
        if (timeComparison != 0)
        {
            return timeComparison < 0;
        }

        var versionComparison = unit.Version.CompareTo(cursor.Version);
        if (versionComparison != 0)
        {
            return versionComparison < 0;
        }

        var unitKindComparison = unit.UnitKind.CompareTo(cursor.UnitKind);
        return unitKindComparison != 0
            ? unitKindComparison < 0
            : string.CompareOrdinal(unit.UnitId, cursor.UnitId) < 0;
    }

    private static bool IsAfterCursor(
        ConfigurationMutationGroup group,
        ConfigurationMutationGroupCursor cursor)
    {
        var timeComparison = group.CreatedTime.CompareTo(cursor.CreatedTime);
        return timeComparison != 0
            ? timeComparison < 0
            : string.CompareOrdinal(group.GroupId, cursor.GroupId) < 0;
    }

    private sealed record HistoryPageUnitKey(
        ConfigurationHistoryUnitKind UnitKind,
        string UnitId);

    private sealed record HistoryPageUnit(
        ConfigurationHistoryUnitKind UnitKind,
        string UnitId,
        IReadOnlyList<ConfigurationValueHistory> Rows)
    {
        public DateTimeOffset ModifiedTime => Rows.Max(static history => history.ModifiedTime);

        public long Version => Rows.Max(static history => history.Version);
    }

    private sealed record DocumentMetadataDto
    {
        public string DefinitionKey { get; init; } = "";

        public long Version { get; init; }

        public int SchemaVersion { get; init; }

        public DateTimeOffset LastModifiedTime { get; init; }

        public string? LastModifierId { get; init; }

        public string? LastModifierName { get; init; }
    }

    private sealed record PublishedDefinitionDto
    {
        public string DefinitionKey { get; init; } = "";

        public string SectionPath { get; init; } = "";

        public string DisplayName { get; init; } = "";

        public string? Description { get; init; }

        public string ClrTypeName { get; init; } = "";

        public string FromProject { get; init; } = "";

        public string? Category { get; init; }

        public int SchemaVersion { get; init; }

        public int DefinitionRevision { get; init; }

        public string SchemaHash { get; init; } = "";

        public string ReloadBehavior { get; init; } = "";

        public string SchemaJson { get; init; } = "";

        public PublishedDefinitionPublisherStateDto? PublisherState { get; init; }

        public static PublishedDefinitionDto FromPublication(
            ConfigurationDefinition definition,
            string publisherKey,
            ConfigurationReloadBehaviorObservation observation,
            PublishedDefinitionDto? existing)
        {
            var candidate = new PublishedDefinitionDto
            {
                DefinitionKey = definition.DefinitionKey,
                SectionPath = definition.SectionPath,
                DisplayName = definition.DisplayName,
                Description = NullIfWhiteSpace(definition.Description),
                ClrTypeName = ConfigurationDefinitionSchemaCodec.ToCompactClrTypeName(definition.ClrTypeName),
                FromProject = definition.FromProject,
                Category = NullIfWhiteSpace(definition.Category),
                SchemaVersion = ResolvePublishedSchemaVersion(definition, existing),
                SchemaHash = definition.SchemaHash,
                ReloadBehavior = definition.ReloadBehavior.ToString(),
                SchemaJson = ConfigurationDefinitionSchemaCodec.SerializeSchema(definition),
                PublisherState = PublishedDefinitionPublisherStateDto.FromObservation(publisherKey, observation)
            };
            var definitionRevision = existing is null
                ? 1
                : candidate.HasSameCanonicalSnapshot(existing)
                    ? Math.Max(existing.DefinitionRevision, 1)
                    : checked(Math.Max(existing.DefinitionRevision, 0) + 1);

            return candidate with
            {
                DefinitionRevision = definitionRevision
            };
        }

        public bool Matches(PublishedDefinitionDto existing)
        {
            return string.Equals(DefinitionKey, existing.DefinitionKey, StringComparison.Ordinal)
                   && string.Equals(SectionPath, existing.SectionPath, StringComparison.Ordinal)
                   && string.Equals(DisplayName, existing.DisplayName, StringComparison.Ordinal)
                   && string.Equals(Description, NullIfWhiteSpace(existing.Description), StringComparison.Ordinal)
                   && string.Equals(ClrTypeName, existing.ClrTypeName, StringComparison.Ordinal)
                   && string.Equals(FromProject, existing.FromProject, StringComparison.Ordinal)
                   && string.Equals(Category, NullIfWhiteSpace(existing.Category), StringComparison.Ordinal)
                   && SchemaVersion == existing.SchemaVersion
                   && DefinitionRevision == existing.DefinitionRevision
                   && string.Equals(SchemaHash, existing.SchemaHash, StringComparison.Ordinal)
                   && string.Equals(ReloadBehavior, existing.ReloadBehavior, StringComparison.Ordinal)
                   && string.Equals(SchemaJson, existing.SchemaJson, StringComparison.Ordinal)
                   && Equals(PublisherState, existing.PublisherState);
        }

        public PublishedDefinitionDto WithdrawPublisher(string publisherKey)
        {
            if (PublisherState is null
                || !string.Equals(PublisherState.PublisherKey, publisherKey, StringComparison.OrdinalIgnoreCase))
            {
                return this;
            }

            var withdrawn = this with
            {
                PublisherState = null,
                ReloadBehavior = ConfigurationReloadBehavior.Unknown.ToString()
            };
            return withdrawn with
            {
                DefinitionRevision = withdrawn.HasSameCanonicalSnapshot(this)
                    ? Math.Max(DefinitionRevision, 1)
                    : checked(Math.Max(DefinitionRevision, 0) + 1)
            };
        }

        public ConfigurationPublishedDefinitionRecord ToRecord(string storeKey)
        {
            return new ConfigurationPublishedDefinitionRecord
            {
                StoreKey = storeKey,
                DefinitionKey = DefinitionKey,
                LifecycleState = PublisherState is null
                    ? ConfigurationDefinitionLifecycleState.Retired
                    : ConfigurationDefinitionLifecycleState.Active,
                SectionPath = SectionPath,
                DisplayName = DisplayName,
                Description = Description,
                ClrTypeName = ClrTypeName,
                FromProject = FromProject,
                Category = Category,
                SchemaVersion = SchemaVersion,
                DefinitionRevision = DefinitionRevision,
                SchemaHash = SchemaHash,
                ReloadBehavior = ReloadBehavior,
                SchemaJson = SchemaJson
            };
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private bool HasSameCanonicalSnapshot(PublishedDefinitionDto existing)
        {
            return string.Equals(DefinitionKey, existing.DefinitionKey, StringComparison.Ordinal)
                   && string.Equals(SectionPath, existing.SectionPath, StringComparison.Ordinal)
                   && string.Equals(DisplayName, existing.DisplayName, StringComparison.Ordinal)
                   && string.Equals(Description, NullIfWhiteSpace(existing.Description), StringComparison.Ordinal)
                   && string.Equals(ClrTypeName, existing.ClrTypeName, StringComparison.Ordinal)
                   && string.Equals(FromProject, existing.FromProject, StringComparison.Ordinal)
                   && string.Equals(Category, NullIfWhiteSpace(existing.Category), StringComparison.Ordinal)
                   && SchemaVersion == existing.SchemaVersion
                   && string.Equals(SchemaHash, existing.SchemaHash, StringComparison.Ordinal)
                   && string.Equals(ReloadBehavior, existing.ReloadBehavior, StringComparison.Ordinal)
                   && string.Equals(SchemaJson, existing.SchemaJson, StringComparison.Ordinal);
        }

        private static int ResolvePublishedSchemaVersion(
            ConfigurationDefinition definition,
            PublishedDefinitionDto? existing)
        {
            if (existing is null)
            {
                return Math.Max(definition.SchemaVersion, 1);
            }

            var currentVersion = Math.Max(existing.SchemaVersion, 1);
            return string.Equals(existing.SchemaHash, definition.SchemaHash, StringComparison.Ordinal)
                ? currentVersion
                : checked(Math.Max(currentVersion, definition.SchemaVersion) + 1);
        }

    }

    private sealed record PublishedDefinitionPublisherStateDto
    {
        public string PublisherKey { get; init; } = "";

        public string ObservationKind { get; init; } = "";

        public string ReloadBehavior { get; init; } = "";

        public static PublishedDefinitionPublisherStateDto FromObservation(
            string publisherKey,
            ConfigurationReloadBehaviorObservation observation)
        {
            return new PublishedDefinitionPublisherStateDto
            {
                PublisherKey = publisherKey,
                ObservationKind = observation.Kind.ToString(),
                ReloadBehavior = observation.Behavior.ToString()
            };
        }

        public ConfigurationDefinitionPublisherState ToModel()
        {
            if (!Enum.TryParse<ConfigurationReloadBehaviorObservationKind>(
                    ObservationKind,
                    ignoreCase: false,
                    out var observationKind)
                || !Enum.IsDefined(observationKind))
            {
                throw new InvalidDataException(
                    $"Published definition publisher state uses unsupported observation kind '{ObservationKind}'.");
            }

            if (!Enum.TryParse<ConfigurationReloadBehavior>(
                    ReloadBehavior,
                    ignoreCase: false,
                    out var reloadBehavior)
                || !Enum.IsDefined(reloadBehavior))
            {
                throw new InvalidDataException(
                    $"Published definition publisher state uses unsupported reload behavior '{ReloadBehavior}'.");
            }

            _ = new ConfigurationReloadBehaviorObservation(observationKind, reloadBehavior);
            return new ConfigurationDefinitionPublisherState
            {
                PublisherKey = PublisherKey,
                ObservationKind = observationKind,
                ReloadBehavior = reloadBehavior
            };
        }
    }

    private sealed record HistoryDto
    {
        public string HistoryId { get; init; } = "";

        public string DefinitionKey { get; init; } = "";

        public string LogicalPath { get; init; } = "";

        public string? ConfigurationPath { get; init; }

        public string TargetKind { get; init; } = "";

        public string? SourceProviderType { get; init; }

        public string? SourceDisplayName { get; init; }

        public string? SourcePhysicalPath { get; init; }

        public string? SourceConfigurationPath { get; init; }

        public string MutationKind { get; init; } = "";

        public string Granularity { get; init; } = "";

        public string State { get; init; } = "";

        public ConfigurationStoredValue? OldValue { get; init; }

        public ConfigurationStoredValue NewValue { get; init; } = ConfigurationStoredValue.Null;

        public long Version { get; init; }

        public string? SourceRevisionBefore { get; init; }

        public string? SourceRevisionAfter { get; init; }

        public int SchemaVersion { get; init; }

        public string? SchemaHash { get; init; }

        public DateTimeOffset ModifiedTime { get; init; }

        public string? ModifierId { get; init; }

        public string? ModifierName { get; init; }

        public string? Reason { get; init; }

        public string? MutationGroupId { get; init; }

        public static HistoryDto FromHistory(ConfigurationValueHistory history)
        {
            return new HistoryDto
            {
                HistoryId = history.HistoryId,
                DefinitionKey = history.DefinitionKey,
                LogicalPath = history.LogicalPath.ToCanonicalString(),
                ConfigurationPath = history.ConfigurationPath,
                TargetKind = history.TargetKind.ToString(),
                SourceProviderType = history.SourceProviderType,
                SourceDisplayName = history.SourceDisplayName,
                SourcePhysicalPath = history.SourcePhysicalPath,
                SourceConfigurationPath = history.SourceConfigurationPath,
                MutationKind = history.MutationKind.ToString(),
                Granularity = history.Granularity.ToString(),
                State = history.State.ToString(),
                OldValue = history.OldValue,
                NewValue = history.NewValue,
                Version = history.Version,
                SourceRevisionBefore = history.SourceRevisionBefore,
                SourceRevisionAfter = history.SourceRevisionAfter,
                SchemaVersion = history.SchemaVersion,
                SchemaHash = history.SchemaHash,
                ModifiedTime = history.ModifiedTime,
                ModifierId = history.ModifierId,
                ModifierName = history.ModifierName,
                Reason = history.Reason,
                MutationGroupId = history.MutationGroupId
            };
        }

        public ConfigurationValueHistory ToHistory()
        {
            return new ConfigurationValueHistory
            {
                HistoryId = HistoryId,
                DefinitionKey = DefinitionKey,
                LogicalPath = string.IsNullOrWhiteSpace(LogicalPath) ? Models.LogicalPath.Root : Models.LogicalPath.Parse(LogicalPath),
                ConfigurationPath = ConfigurationPath,
                TargetKind = string.IsNullOrWhiteSpace(TargetKind)
                    ? ConfigurationMutationTargetKind.MonicaEffectiveStore
                    : Enum.Parse<ConfigurationMutationTargetKind>(TargetKind),
                SourceProviderType = SourceProviderType,
                SourceDisplayName = SourceDisplayName,
                SourcePhysicalPath = SourcePhysicalPath,
                SourceConfigurationPath = SourceConfigurationPath,
                MutationKind = Enum.Parse<ConfigurationMutationKind>(MutationKind),
                Granularity = Enum.Parse<ConfigurationMutationGranularity>(Granularity),
                State = Enum.Parse<ConfigurationValueState>(State),
                OldValue = OldValue,
                NewValue = NewValue,
                Version = Version,
                SourceRevisionBefore = SourceRevisionBefore,
                SourceRevisionAfter = SourceRevisionAfter,
                SchemaVersion = SchemaVersion,
                SchemaHash = SchemaHash,
                ModifiedTime = ModifiedTime,
                ModifierId = ModifierId,
                ModifierName = ModifierName,
                Reason = Reason,
                MutationGroupId = MutationGroupId
            };
        }
    }

    private sealed record GroupDto
    {
        public string GroupId { get; init; } = "";

        public string Label { get; init; } = "";

        public string? Reason { get; init; }

        public IReadOnlyList<string> DefinitionKeys { get; init; } = [];

        public int MutationCount { get; init; }

        public DateTimeOffset CreatedTime { get; init; }

        public string? ModifierId { get; init; }

        public string? ModifierName { get; init; }

        public DateTimeOffset? RolledBackTime { get; init; }

        public string? RolledBackGroupId { get; init; }

        public string Status { get; init; } = "";

        public static GroupDto FromGroup(ConfigurationMutationGroup group)
        {
            return new GroupDto
            {
                GroupId = group.GroupId,
                Label = group.Label,
                Reason = group.Reason,
                DefinitionKeys = group.DefinitionKeys,
                MutationCount = group.MutationCount,
                CreatedTime = group.CreatedTime,
                ModifierId = group.ModifierId,
                ModifierName = group.ModifierName,
                RolledBackTime = group.RolledBackTime,
                RolledBackGroupId = group.RolledBackGroupId,
                Status = group.Status.ToString()
            };
        }

        public ConfigurationMutationGroup ToGroup()
        {
            return new ConfigurationMutationGroup
            {
                GroupId = GroupId,
                Label = Label,
                Reason = Reason,
                DefinitionKeys = DefinitionKeys,
                MutationCount = MutationCount,
                CreatedTime = CreatedTime,
                ModifierId = ModifierId,
                ModifierName = ModifierName,
                RolledBackTime = RolledBackTime,
                RolledBackGroupId = RolledBackGroupId,
                Status = Enum.Parse<ConfigurationMutationGroupStatus>(Status)
            };
        }
    }
}
