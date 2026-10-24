using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Models.Internal;
using Monica.Core.Extensions;

namespace Monica.Configuration.Services;

/// <summary>
/// Resolves configuration definitions from local scanner metadata and the published metadata store.
/// </summary>
public sealed class ConfigurationDefinitionResolver(
    IConfigurationDefinitionRegistry definitionRegistry,
    IConfigurationMetadataStore? metadataStore = null)
{
    private readonly Func<IConfigurationMetadataStore?>? _metadataStoreFactory;
    private IConfigurationMetadataStore? MetadataStore => _metadataStoreFactory is null ? metadataStore : _metadataStoreFactory();

    internal ConfigurationDefinitionResolver(IConfigurationDefinitionRegistry registry, Func<IConfigurationMetadataStore?> metadataStoreFactory)
        : this(registry) => _metadataStoreFactory = metadataStoreFactory;

    private static readonly TimeSpan READ_SNAPSHOT_LIFETIME = TimeSpan.FromSeconds(30);
    private readonly Lock _readSnapshotLock = new();
    private ConfigurationDefinitionReadSnapshot? _readSnapshot;
    private long _readSnapshotGeneration;

    /// <summary>
    /// Gets a fault-isolated catalog of active and retired definitions for diagnostic UI scenarios without weakening
    /// authoritative resolution.
    /// </summary>
    internal async Task<ConfigurationDefinitionCatalogSnapshot> GetDiagnosticCatalogAsync(
        CancellationToken cancellationToken)
    {
        var snapshotGeneration = CaptureReadSnapshotGeneration();
        var entriesByKey = new Dictionary<string, ConfigurationDefinitionCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var registeredDefinition in definitionRegistry.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = registeredDefinition with { Origin = ConfigurationDefinitionOrigin.LocalScan };
            entriesByKey[definition.DefinitionKey] = new ConfigurationDefinitionCatalogEntry
            {
                Definition = definition,
                Availability = ConfigurationDefinitionAvailability.Available,
                LifecycleState = ConfigurationDefinitionLifecycleState.Active
            };
        }

        IReadOnlyList<ConfigurationPublishedDefinitionEntry> publishedEntries = [];
        IConfigurationMetadataStore? store = null;
        try
        {
            store = MetadataStore;
            if (store is not null)
                publishedEntries = await store.ListPublishedDefinitionEntriesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            InvalidateReadSnapshot();
            foreach (var (key, entry) in entriesByKey.ToArray())
            {
                if (entry.Definition?.Kind == ConfigurationKind.Startup) continue;
                entriesByKey[key] = entry with
                {
                    Availability = ConfigurationDefinitionAvailability.AvailableWithMetadataFault
                };
            }

            return new ConfigurationDefinitionCatalogSnapshot
            {
                Entries = SortCatalogEntries(entriesByKey.Values),
                StoreDiagnostic = CreateStoreDiagnostic(store, ex)
            };
        }

        foreach (var publishedEntry in publishedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definitionKey = publishedEntry.Metadata.DefinitionKey;
            if (entriesByKey.TryGetValue(definitionKey, out var startupEntry)
                && startupEntry.Definition?.Kind == ConfigurationKind.Startup) continue;
            if (!publishedEntry.IsAvailable)
            {
                if (entriesByKey.TryGetValue(definitionKey, out var localEntry)
                    && localEntry.Definition is not null)
                {
                    entriesByKey[definitionKey] = localEntry with
                    {
                        PublishedMetadata = publishedEntry.Metadata,
                        Diagnostic = publishedEntry.Diagnostic,
                        Availability = ConfigurationDefinitionAvailability.AvailableWithMetadataFault,
                        LifecycleState = ConfigurationDefinitionLifecycleState.Active
                    };
                }
                else
                {
                    entriesByKey[definitionKey] = new ConfigurationDefinitionCatalogEntry
                    {
                        PublishedMetadata = publishedEntry.Metadata,
                        Diagnostic = publishedEntry.Diagnostic,
                        Availability = ConfigurationDefinitionAvailability.Unavailable,
                        LifecycleState = publishedEntry.Metadata.LifecycleState
                    };
                }

                continue;
            }

            var publishedDefinition = publishedEntry.RequireDefinition();
            if (entriesByKey.TryGetValue(definitionKey, out var existing)
                && existing.Definition is { } localDefinition)
            {
                entriesByKey[definitionKey] = new ConfigurationDefinitionCatalogEntry
                {
                    Definition = MergePublishedMetadata(
                        localDefinition,
                        publishedDefinition,
                        publishedEntry.Metadata.LifecycleState),
                    PublishedMetadata = publishedEntry.Metadata,
                    Availability = ConfigurationDefinitionAvailability.Available,
                    LifecycleState = ConfigurationDefinitionLifecycleState.Active
                };
                continue;
            }

            entriesByKey[definitionKey] = new ConfigurationDefinitionCatalogEntry
            {
                Definition = publishedDefinition with { Origin = ConfigurationDefinitionOrigin.PublishedMetadata },
                PublishedMetadata = publishedEntry.Metadata,
                Availability = ConfigurationDefinitionAvailability.Available,
                LifecycleState = publishedEntry.Metadata.LifecycleState
            };
        }

        var snapshot = new ConfigurationDefinitionCatalogSnapshot
        {
            Entries = SortCatalogEntries(entriesByKey.Values)
        };
        PublishReadSnapshot(snapshot, snapshotGeneration);
        return snapshot;
    }

    /// <summary>
    /// Gets all operational definitions, preferring local metadata on key collisions and excluding publisherless
    /// retired definitions.
    /// </summary>
    public async Task<IReadOnlyList<ConfigurationDefinition>> GetMergedDefinitionsAsync(CancellationToken cancellationToken)
    {
        var localDefinitions = definitionRegistry.GetAll()
            .ToArray();
        var definitionsByKey = new Dictionary<string, ConfigurationDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in localDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            definitionsByKey[definition.DefinitionKey] = definition with { Origin = ConfigurationDefinitionOrigin.LocalScan };
        }

        var store = MetadataStore;
        IReadOnlyList<ConfigurationPublishedDefinitionEntry> publishedEntries = store is null
            ? []
            : await store.ListPublishedDefinitionEntriesAsync(cancellationToken);
        foreach (var entry in publishedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definitionsByKey.TryGetValue(entry.Metadata.DefinitionKey, out var startup)
                && startup.Kind == ConfigurationKind.Startup) continue;
            if (entry.Metadata.LifecycleState == ConfigurationDefinitionLifecycleState.Retired
                && !definitionsByKey.ContainsKey(entry.Metadata.DefinitionKey))
            {
                continue;
            }

            var published = entry.RequireDefinition();
            if (definitionsByKey.TryGetValue(published.DefinitionKey, out var localDefinition))
            {
                definitionsByKey[published.DefinitionKey] = MergePublishedMetadata(
                    localDefinition,
                    published,
                    entry.Metadata.LifecycleState);
                continue;
            }

            definitionsByKey[published.DefinitionKey] = published with { Origin = ConfigurationDefinitionOrigin.PublishedMetadata };
        }

        return definitionsByKey.Values
            .OrderBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets one active local or published definition, preferring local metadata.
    /// </summary>
    public async Task<ConfigurationDefinition> GetRequiredAsync(string definitionKey, CancellationToken cancellationToken)
    {
        if (definitionRegistry.TryGet(definitionKey, out var localDefinition))
        {
            var local = localDefinition!;
            if (local.Kind == ConfigurationKind.Startup) return local;
            if (MetadataStore is null)
            {
                return MergePublishedMetadata(
                    local with { Origin = ConfigurationDefinitionOrigin.LocalScan },
                    publishedDefinition: null,
                    publishedLifecycleState: null);
            }

            var localPublishedEntry = await MetadataStore.GetPublishedDefinitionEntryAsync(
                local.DefinitionKey,
                cancellationToken);
            return MergePublishedMetadata(
                local with { Origin = ConfigurationDefinitionOrigin.LocalScan },
                localPublishedEntry?.RequireDefinition(),
                localPublishedEntry?.Metadata.LifecycleState);
        }

        if (MetadataStore is null)
        {
            throw new ConfigurationDefinitionNotFoundException(definitionKey);
        }

        var publishedEntry = await MetadataStore.GetPublishedDefinitionEntryAsync(definitionKey, cancellationToken);
        return publishedEntry is null
               || publishedEntry.Metadata.LifecycleState == ConfigurationDefinitionLifecycleState.Retired
            ? throw new ConfigurationDefinitionNotFoundException(definitionKey)
            : publishedEntry.RequireDefinition() with { Origin = ConfigurationDefinitionOrigin.PublishedMetadata };
    }

    /// <summary>
    /// Gets one definition from the most recent complete management read snapshot when available.
    /// </summary>
    /// <remarks>
    /// This read-optimized path is intended for display and inspection only. Mutation and rollback workflows must use
    /// <see cref="GetRequiredAsync"/> so schema concurrency validation always starts from authoritative active metadata.
    /// Unlike the operational path, this method can return a retired persisted definition for diagnostics.
    /// </remarks>
    internal async Task<ConfigurationDefinition> GetRequiredForReadAsync(
        string definitionKey,
        CancellationToken cancellationToken)
    {
        ConfigurationDefinitionReadSnapshot? snapshot;
        lock (_readSnapshotLock)
        {
            snapshot = _readSnapshot;
        }

        if (snapshot?.IsFresh is true)
        {
            return snapshot.GetRequired(definitionKey);
        }

        var catalog = await GetDiagnosticCatalogAsync(cancellationToken);
        return ConfigurationDefinitionReadSnapshot.Create(catalog.Entries).GetRequired(definitionKey);
    }

    /// <summary>
    /// Invalidates the management read snapshot after locally published or remotely observed metadata changes.
    /// </summary>
    internal void InvalidateReadSnapshot()
    {
        lock (_readSnapshotLock)
        {
            _readSnapshot = null;
            _readSnapshotGeneration++;
        }
    }

    private static ConfigurationDefinition MergePublishedMetadata(
        ConfigurationDefinition localDefinition,
        ConfigurationDefinition? publishedDefinition,
        ConfigurationDefinitionLifecycleState? publishedLifecycleState)
    {
        if (publishedDefinition is null)
        {
            return localDefinition with
            {
                SchemaVersion = Math.Max(localDefinition.SchemaVersion, 1),
                DefinitionRevision = Math.Max(localDefinition.DefinitionRevision, 0)
            };
        }

        return localDefinition with
        {
            SchemaVersion = ResolveLocalSchemaVersion(localDefinition, publishedDefinition),
            DefinitionRevision = publishedDefinition.DefinitionRevision,
            ReloadBehavior = publishedLifecycleState == ConfigurationDefinitionLifecycleState.Retired
                ? localDefinition.ReloadBehavior
                : ResolveMergedReloadBehavior(localDefinition, publishedDefinition)
        };
    }

    private static ConfigurationReloadBehavior ResolveMergedReloadBehavior(
        ConfigurationDefinition localDefinition,
        ConfigurationDefinition publishedDefinition)
    {
        var publishedObservation = publishedDefinition.ReloadBehavior is ConfigurationReloadBehavior.OnlineReloadable
            or ConfigurationReloadBehavior.RequiresRestart
            or ConfigurationReloadBehavior.StaticAfterStartup
                ? ConfigurationReloadBehaviorObservation.Inferred(publishedDefinition.ReloadBehavior)
                : ConfigurationReloadBehaviorObservation.Unresolved();

        return ConfigurationReloadBehaviorObservation.Aggregate(
            [ConfigurationReloadBehaviorObservation.FromDefinition(localDefinition), publishedObservation]);
    }

    private static int ResolveLocalSchemaVersion(
        ConfigurationDefinition localDefinition,
        ConfigurationDefinition publishedDefinition)
    {
        var localVersion = Math.Max(localDefinition.SchemaVersion, 1);
        if (!HasSameSchemaHash(localDefinition, publishedDefinition))
        {
            // A differing local schema is the next unpublished revision; reusing the scanner's default version
            // could make historical rows appear compatible with a different sensitivity contract.
            return checked(Math.Max(localVersion, publishedDefinition.SchemaVersion) + 1);
        }

        return Math.Max(localVersion, publishedDefinition.SchemaVersion);
    }

    private static bool HasSameSchemaHash(
        ConfigurationDefinition localDefinition,
        ConfigurationDefinition publishedDefinition)
    {
        return string.Equals(localDefinition.SchemaHash, publishedDefinition.SchemaHash, StringComparison.Ordinal);
    }

    private long CaptureReadSnapshotGeneration()
    {
        lock (_readSnapshotLock)
        {
            return _readSnapshotGeneration;
        }
    }

    private void PublishReadSnapshot(ConfigurationDefinitionCatalogSnapshot snapshot, long expectedGeneration)
    {
        lock (_readSnapshotLock)
        {
            if (_readSnapshotGeneration == expectedGeneration)
            {
                _readSnapshot = ConfigurationDefinitionReadSnapshot.Create(snapshot.Entries);
            }
        }
    }

    private static ConfigurationMetadataStoreDiagnostic CreateStoreDiagnostic(
        IConfigurationMetadataStore? store,
        Exception error)
    {
        return new ConfigurationMetadataStoreDiagnostic
        {
            StoreKey = store?.Descriptor.StoreKey ?? "configured-metadata-store",
            StoreDisplayName = store?.Descriptor.DisplayName ?? "Configured metadata store",
            Kind = ClassifyStoreFailure(error),
            ErrorType = error.GetType().FullName ?? error.GetType().Name,
            Message = store is null ? "The configured metadata store could not be initialized." : error.GetMessageRecursively()
        };
    }

    private static ConfigurationMetadataStoreIssueKind ClassifyStoreFailure(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ConfigurationStoreSchemaException:
                    return ConfigurationMetadataStoreIssueKind.IncompatibleStoreSchema;
                case ConfigurationMetadataStoreReadException classified:
                    return classified.Kind;
                case UnauthorizedAccessException:
                    return ConfigurationMetadataStoreIssueKind.AccessDenied;
                case DbException or IOException or TimeoutException:
                    return ConfigurationMetadataStoreIssueKind.Unavailable;
            }
        }

        return ConfigurationMetadataStoreIssueKind.ReadFailed;
    }

    private static IReadOnlyList<ConfigurationDefinitionCatalogEntry> SortCatalogEntries(
        IEnumerable<ConfigurationDefinitionCatalogEntry> entries)
    {
        return entries
            .OrderBy(DisplayNameOrKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(DefinitionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string DisplayNameOrKey(ConfigurationDefinitionCatalogEntry entry)
    {
        var displayName = entry.Definition?.DisplayName ?? entry.PublishedMetadata?.DisplayName;
        return string.IsNullOrWhiteSpace(displayName) ? DefinitionKey(entry) : displayName;
    }

    private static string DefinitionKey(ConfigurationDefinitionCatalogEntry entry)
    {
        return entry.Definition?.DefinitionKey ?? entry.PublishedMetadata?.DefinitionKey ?? string.Empty;
    }

    /// <summary>
    /// Attempts to get a locally scanned definition.
    /// </summary>
    public bool TryGetLocal(string definitionKey, [NotNullWhen(true)] out ConfigurationDefinition? definition)
    {
        if (definitionRegistry.TryGet(definitionKey, out var localDefinition) && localDefinition is not null)
        {
            definition = localDefinition with { Origin = ConfigurationDefinitionOrigin.LocalScan };
            return true;
        }

        definition = null;
        return false;
    }

    private sealed class ConfigurationDefinitionReadSnapshot(
        IReadOnlyDictionary<string, ConfigurationDefinitionCatalogEntry> entriesByKey,
        long createdTimestamp)
    {
        public bool IsFresh => Stopwatch.GetElapsedTime(createdTimestamp) < READ_SNAPSHOT_LIFETIME;

        public static ConfigurationDefinitionReadSnapshot Create(
            IReadOnlyList<ConfigurationDefinitionCatalogEntry> entries)
        {
            return new ConfigurationDefinitionReadSnapshot(entries.ToDictionary(
                DefinitionKey,
                StringComparer.OrdinalIgnoreCase), Stopwatch.GetTimestamp());
        }

        public ConfigurationDefinition GetRequired(string definitionKey)
        {
            if (!entriesByKey.TryGetValue(definitionKey, out var entry))
            {
                throw new ConfigurationDefinitionNotFoundException(definitionKey);
            }

            if (entry.Availability != ConfigurationDefinitionAvailability.Available)
            {
                throw new ConfigurationDefinitionMetadataUnavailableException(
                    definitionKey,
                    entry.Diagnostic
                    ?? throw new InvalidOperationException(
                        $"Unavailable definition '{definitionKey}' has no metadata diagnostic."));
            }

            return entry.Definition
                   ?? throw new InvalidOperationException(
                       $"Available definition '{definitionKey}' has no materialized schema.");
        }
    }
}
