using Microsoft.EntityFrameworkCore;
using Monica.Modules;

namespace Monica.Repository.Persistence.Models;

/// <summary>
/// Describes a Repository DbContext registration captured when the host calls
/// <c>AddRepositoryDbContext&lt;TDbContext&gt;</c>.
/// </summary>
public sealed record RepositoryDbContextRegistration
{
    /// <summary>
    /// Creates a new registration descriptor.
    /// </summary>
    /// <param name="dbContextType">The registered DbContext type.</param>
    /// <param name="providerType">How repositories resolve the DbContext in scoped operations.</param>
    public RepositoryDbContextRegistration(
        Type dbContextType,
        DbContextProviderType providerType)
    {
        DbContextType = dbContextType;
        ProviderType = providerType;
    }

    /// <summary>
    /// Stable identifier used by diagnostics callers to select this DbContext.
    /// </summary>
    public string ContextId => DbContextType.AssemblyQualifiedName ?? DbContextType.FullName ?? DbContextType.Name;

    /// <summary>
    /// Registered DbContext type.
    /// </summary>
    public Type DbContextType { get; }

    /// <summary>
    /// Human-readable DbContext name.
    /// </summary>
    public string DbContextName => DbContextType.Name;

    /// <summary>
    /// Fully qualified DbContext type name.
    /// </summary>
    public string FullName => DbContextType.FullName ?? DbContextType.Name;

    /// <summary>
    /// Assembly containing the DbContext type.
    /// </summary>
    public string AssemblyName => DbContextType.Assembly.GetName().Name ?? string.Empty;

    /// <summary>
    /// How repositories resolve the DbContext in scoped operations.
    /// </summary>
    public DbContextProviderType ProviderType { get; }

}

/// <summary>
/// Runtime snapshot for a registered Repository DbContext.
/// </summary>
public sealed record RepositoryDbContextSnapshot
{
    /// <summary>
    /// Static registration metadata.
    /// </summary>
    public required RepositoryDbContextRegistration Registration { get; init; }

    /// <summary>
    /// EF Core provider name reported by the DbContext.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Migration assembly name when EF Core exposes one.
    /// </summary>
    public string? MigrationAssembly { get; init; }

    /// <summary>
    /// Database connection details prepared for diagnostics display.
    /// </summary>
    public RepositoryConnectionInfo? Connection { get; init; }

    /// <summary>
    /// Whether the database accepted a connection probe.
    /// </summary>
    public bool CanConnect { get; init; }

    /// <summary>
    /// Number of pending migrations when migration metadata can be read.
    /// </summary>
    public int? PendingMigrationCount { get; init; }

    /// <summary>
    /// Number of known migrations when migration metadata can be read.
    /// </summary>
    public int? MigrationCount { get; init; }

    /// <summary>
    /// Migration assembly and history evidence captured with this registration. A failed history
    /// read is represented by its error. A snapshot that could not resolve its context has no status.
    /// </summary>
    public RepositoryMigrationStatus? MigrationStatus { get; init; }

    /// <summary>
    /// Flattened Repository and EF option entries suitable for property-grid display.
    /// </summary>
    public IReadOnlyList<RepositoryOptionEntry> Options { get; init; } = [];

    /// <summary>
    /// Error message captured while building this snapshot.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether snapshot collection hit an error for this context.
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
}

/// <summary>
/// Safe connection-string and connection endpoint details for a Repository DbContext.
/// </summary>
public sealed record RepositoryConnectionInfo
{
    /// <summary>
    /// Provider-specific data source, server, or host value.
    /// </summary>
    public string? DataSource { get; init; }

    /// <summary>
    /// Database name reported by the provider.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>
    /// Connection pool settings parsed from the connection string.
    /// <c>null</c> when the string cannot be parsed for pool keywords.
    /// </summary>
    public RepositoryConnectionPoolInfo? Pool { get; init; }

    /// <summary>
    /// Masked connection string with sensitive values replaced.
    /// </summary>
    public string? MaskedConnectionString { get; init; }

    /// <summary>
    /// Full connection string. This is populated only when a caller explicitly requests reveal.
    /// </summary>
    public string? RevealedConnectionString { get; init; }

    /// <summary>
    /// Whether the full connection string is present.
    /// </summary>
    public bool IsRevealed => !string.IsNullOrEmpty(RevealedConnectionString);
}

/// <summary>
/// Connection pool settings parsed from a Repository DbContext connection string,
/// with shared ADO.NET provider defaults applied where the string omits explicit values.
/// Mainstream providers (Npgsql, Microsoft.Data.SqlClient, MySqlConnector) share the same defaults.
/// </summary>
public sealed record RepositoryConnectionPoolInfo
{
    /// <summary>
    /// Maximum pool size applied when the connection string omits it.
    /// </summary>
    public const int DefaultMaxPoolSize = 100;

    /// <summary>
    /// Minimum pool size applied when the connection string omits it.
    /// </summary>
    public const int DefaultMinPoolSize = 0;

    /// <summary>
    /// Explicit pooling switch from the connection string.
    /// <c>null</c> when not set; providers default to enabled pooling.
    /// </summary>
    public bool? Pooling { get; init; }

    /// <summary>
    /// Explicitly configured maximum pool size.
    /// <c>null</c> when the connection string does not set it.
    /// </summary>
    public int? MaxPoolSize { get; init; }

    /// <summary>
    /// Explicitly configured minimum pool size.
    /// <c>null</c> when the connection string does not set it.
    /// </summary>
    public int? MinPoolSize { get; init; }

    /// <summary>
    /// Whether pooling is effectively enabled for the connection.
    /// </summary>
    public bool PoolingEnabled => Pooling ?? true;

    /// <summary>
    /// Maximum pool size that applies after defaults.
    /// Only meaningful while <see cref="PoolingEnabled"/> is <c>true</c>.
    /// </summary>
    public int EffectiveMaxPoolSize => MaxPoolSize ?? DefaultMaxPoolSize;

    /// <summary>
    /// Minimum pool size that applies after defaults.
    /// Only meaningful while <see cref="PoolingEnabled"/> is <c>true</c>.
    /// </summary>
    public int EffectiveMinPoolSize => MinPoolSize ?? DefaultMinPoolSize;

    /// <summary>
    /// Whether the effective maximum pool size comes from the provider default instead of an explicit setting.
    /// </summary>
    public bool MaxPoolSizeIsDefault => MaxPoolSize is null;

    /// <summary>
    /// Whether the effective minimum pool size comes from the provider default instead of an explicit setting.
    /// </summary>
    public bool MinPoolSizeIsDefault => MinPoolSize is null;
}

/// <summary>
/// Display-ready option entry for Repository diagnostics.
/// </summary>
public sealed record RepositoryOptionEntry
{
    /// <summary>
    /// Option path, such as <c>ModuleRepository.EnableEfCoreConnectionMetrics</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Human-readable option label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Formatted option value.
    /// </summary>
    public required string Value { get; init; }
}

/// <summary>
/// Result of reading database activity for a selected Repository DbContext.
/// </summary>
public sealed record RepositoryActivityResult
{
    /// <summary>
    /// Selected context registration metadata.
    /// </summary>
    public required RepositoryDbContextRegistration Registration { get; init; }

    /// <summary>
    /// Activity support status for the selected provider.
    /// </summary>
    public RepositoryActivitySupport Support { get; init; }

    /// <summary>
    /// Provider name reported by EF Core.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Provider-specific guide that explains how the activity result can be queried manually.
    /// </summary>
    public RepositoryActivityProviderGuide? ProviderGuide { get; init; }

    /// <summary>
    /// All server-visible activity rows, including sessions on other databases, ordered by query age descending.
    /// </summary>
    public IReadOnlyList<RepositoryActivityRow> Rows { get; init; } = [];

    /// <summary>
    /// Aggregated session counts. Totals include every returned row; per-state counts use the context's database when known.
    /// Populated only when the provider activity query is supported and succeeds.
    /// </summary>
    public RepositoryActivitySummary? Summary { get; init; }

    /// <summary>
    /// Error or unsupported-provider message for display.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Number of returned rows.
    /// </summary>
    public int SessionCount => Rows.Count;
}

/// <summary>
/// Aggregated session counts derived from provider activity rows.
/// Totals include every server-visible row. Per-state counts use the selected database when known,
/// including other clients, and do not measure occupancy of the application's local connection pool.
/// </summary>
public sealed record RepositoryActivitySummary
{
    /// <summary>
    /// Creates a summary from activity rows, scoping per-state counts to
    /// <paramref name="databaseName"/> when it is known.
    /// </summary>
    /// <param name="rows">Normalized activity rows returned by the provider query.</param>
    /// <param name="databaseName">
    /// Database name of the inspected context. When empty, all rows are included in the per-state scope.
    /// </param>
    public static RepositoryActivitySummary Create(IReadOnlyList<RepositoryActivityRow> rows, string? databaseName)
    {
        var databaseRows = string.IsNullOrWhiteSpace(databaseName)
            ? rows
            : rows.Where(row => string.Equals(row.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase)).ToList();

        return new RepositoryActivitySummary
        {
            TotalSessions = rows.Count,
            DatabaseSessions = databaseRows.Count,
            ActiveSessions = CountState(databaseRows, "active"),
            IdleSessions = CountState(databaseRows, "idle"),
            IdleInTransactionSessions = CountStatePrefix(databaseRows, "idle in transaction"),
            WaitingSessions = databaseRows.Count(row => row.Waiting == true)
        };
    }

    /// <summary>
    /// Total sessions returned by the activity query, across all databases.
    /// </summary>
    public int TotalSessions { get; init; }

    /// <summary>
    /// Sessions bound to the inspected context's database, including other clients.
    /// </summary>
    public int DatabaseSessions { get; init; }

    /// <summary>
    /// Database sessions currently executing a statement (state <c>active</c>).
    /// </summary>
    public int ActiveSessions { get; init; }

    /// <summary>
    /// Database sessions waiting for the next client request (state <c>idle</c>).
    /// </summary>
    public int IdleSessions { get; init; }

    /// <summary>
    /// Database sessions idle inside an open transaction, including aborted ones.
    /// </summary>
    public int IdleInTransactionSessions { get; init; }

    /// <summary>
    /// Database sessions whose provider waiting projection is true. PostgreSQL reports only
    /// <c>wait_event_type = Lock</c>; openGauss uses its native waiting field. The count can overlap state counts.
    /// </summary>
    public int WaitingSessions { get; init; }

    private static int CountState(IReadOnlyList<RepositoryActivityRow> rows, string state)
    {
        return rows.Count(row => string.Equals(NormalizeState(row.State), state, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountStatePrefix(IReadOnlyList<RepositoryActivityRow> rows, string statePrefix)
    {
        return rows.Count(row => NormalizeState(row.State)
            .StartsWith(statePrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeState(string? state)
    {
        return state?.Trim() ?? string.Empty;
    }
}

/// <summary>
/// Activity support status for a Repository DbContext provider.
/// </summary>
public enum RepositoryActivitySupport
{
    /// <summary>
    /// Activity query is supported and completed.
    /// </summary>
    Supported,

    /// <summary>
    /// The EF provider is not supported by the activity query catalog.
    /// </summary>
    UnsupportedProvider,

    /// <summary>
    /// The activity query failed.
    /// </summary>
    Failed
}

/// <summary>
/// Normalized provider kind used by Repository database activity diagnostics.
/// </summary>
public enum RepositoryActivityProviderKind
{
    /// <summary>
    /// The provider is not recognized by the Repository activity query catalog.
    /// </summary>
    Unsupported,

    /// <summary>
    /// PostgreSQL-compatible activity diagnostics.
    /// </summary>
    PostgreSql,

    /// <summary>
    /// openGauss-compatible activity diagnostics.
    /// </summary>
    OpenGauss
}

/// <summary>
/// Provider-specific manual query guide for Repository database activity diagnostics.
/// </summary>
public sealed record RepositoryActivityProviderGuide
{
    /// <summary>
    /// Provider kind resolved from the EF Core provider name.
    /// </summary>
    public RepositoryActivityProviderKind ProviderKind { get; init; }

    /// <summary>
    /// EF Core provider name reported by the selected DbContext.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Short provider display name suitable for diagnostics UI.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Provider catalog object queried by the Repository activity diagnostic SQL.
    /// </summary>
    public string? SourceObject { get; init; }

    /// <summary>
    /// Read-only SQL statement that can be executed manually against the selected database.
    /// </summary>
    public string? Sql { get; init; }

    /// <summary>
    /// Column aliases selected by <see cref="Sql"/> and normalized by Repository diagnostics.
    /// </summary>
    public IReadOnlyList<string> SelectedColumns { get; init; } = [];

    /// <summary>
    /// Whether Repository has a manual activity SQL template for this provider.
    /// </summary>
    public bool IsSupported => ProviderKind != RepositoryActivityProviderKind.Unsupported
                               && !string.IsNullOrWhiteSpace(Sql);
}

/// <summary>
/// Normalized database activity row used by the Repository UI.
/// </summary>
public sealed record RepositoryActivityRow
{
    /// <summary>
    /// Age since the provider's current or most recent query-start timestamp, including idle sessions.
    /// This is not necessarily the duration of an actively executing statement.
    /// </summary>
    public TimeSpan? Runtime { get; init; }

    /// <summary>
    /// Database name.
    /// </summary>
    public string? DatabaseName { get; init; }

    /// <summary>
    /// Backend process or session identifier.
    /// </summary>
    public string? BackendId { get; init; }

    /// <summary>
    /// User name associated with the session.
    /// </summary>
    public string? UserName { get; init; }

    /// <summary>
    /// Application name associated with the session.
    /// </summary>
    public string? ApplicationName { get; init; }

    /// <summary>
    /// Client address.
    /// </summary>
    public string? ClientAddress { get; init; }

    /// <summary>
    /// Client host name.
    /// </summary>
    public string? ClientHostname { get; init; }

    /// <summary>
    /// Client port.
    /// </summary>
    public int? ClientPort { get; init; }

    /// <summary>
    /// Backend start time.
    /// </summary>
    public DateTime? BackendStart { get; init; }

    /// <summary>
    /// Transaction start time.
    /// </summary>
    public DateTime? TransactionStart { get; init; }

    /// <summary>
    /// Query start time.
    /// </summary>
    public DateTime? QueryStart { get; init; }

    /// <summary>
    /// Last state change time.
    /// </summary>
    public DateTime? StateChange { get; init; }

    /// <summary>
    /// Provider waiting projection. PostgreSQL is true only for <c>wait_event_type = Lock</c>;
    /// false does not exclude other wait categories. openGauss uses its native waiting field.
    /// Null means no waiting value was supplied.
    /// </summary>
    public bool? Waiting { get; init; }

    /// <summary>
    /// Provider-specific state value.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Current or last query text.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Optional provider-specific detail value.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Migration inventory for a selected Repository DbContext.
/// </summary>
public sealed record RepositoryMigrationStatus
{
    /// <summary>
    /// Selected context registration metadata.
    /// </summary>
    public required RepositoryDbContextRegistration Registration { get; init; }

    /// <summary>
    /// EF Core provider name.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Migration assembly name.
    /// </summary>
    public string? MigrationAssembly { get; init; }

    /// <summary>
    /// All migrations known to the DbContext.
    /// </summary>
    public IReadOnlyList<string> AllMigrations { get; init; } = [];

    /// <summary>
    /// Migrations already applied to the database.
    /// </summary>
    public IReadOnlyList<string> AppliedMigrations { get; init; } = [];

    /// <summary>
    /// Migrations still pending.
    /// </summary>
    public IReadOnlyList<string> PendingMigrations { get; init; } = [];

    /// <summary>
    /// Migrations from the selected DbContext migration assembly with their current database state.
    /// </summary>
    public IReadOnlyList<RepositoryMigrationAssemblyItem> CurrentAssemblyMigrations
    {
        get
        {
            var appliedMigrations = AppliedMigrations.ToHashSet(StringComparer.Ordinal);
            var pendingMigrations = PendingMigrations.ToHashSet(StringComparer.Ordinal);

            return AllMigrations
                .Select(migrationId => new RepositoryMigrationAssemblyItem
                {
                    MigrationId = migrationId,
                    State = ResolveCurrentAssemblyMigrationState(migrationId, appliedMigrations, pendingMigrations)
                })
                .ToList();
        }
    }

    /// <summary>
    /// Applied history-table rows that belong to this DbContext's current migration assembly.
    /// </summary>
    public IReadOnlyList<string> AppliedCurrentAssemblyMigrations
    {
        get
        {
            var appliedMigrations = AppliedMigrations.ToHashSet(StringComparer.Ordinal);
            return AllMigrations
                .Where(appliedMigrations.Contains)
                .ToList();
        }
    }

    /// <summary>
    /// Applied history-table rows that are not part of this DbContext's current migration assembly.
    /// </summary>
    public IReadOnlyList<string> ExternalAppliedMigrations
    {
        get
        {
            var currentAssemblyMigrations = AllMigrations.ToHashSet(StringComparer.Ordinal);
            return AppliedMigrations
                .Where(migration => !currentAssemblyMigrations.Contains(migration))
                .ToList();
        }
    }

    /// <summary>
    /// Error message captured while loading migration status.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the migration status could be read.
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// Whether this DbContext has migrations waiting to be applied.
    /// </summary>
    public bool HasPendingMigrations => PendingMigrations.Count > 0;

    /// <summary>
    /// Whether the database migration history contains rows outside this DbContext's current migration assembly.
    /// </summary>
    public bool HasExternalAppliedMigrations => ExternalAppliedMigrations.Count > 0;

    private static RepositoryMigrationApplicationState ResolveCurrentAssemblyMigrationState(
        string migrationId,
        IReadOnlySet<string> appliedMigrations,
        IReadOnlySet<string> pendingMigrations)
    {
        if (pendingMigrations.Contains(migrationId))
        {
            return RepositoryMigrationApplicationState.Pending;
        }

        return appliedMigrations.Contains(migrationId)
            ? RepositoryMigrationApplicationState.Applied
            : RepositoryMigrationApplicationState.Unknown;
    }
}

/// <summary>
/// Display-ready migration entry from the selected DbContext's current migration assembly.
/// </summary>
public sealed record RepositoryMigrationAssemblyItem
{
    /// <summary>
    /// EF Core migration identifier.
    /// </summary>
    public required string MigrationId { get; init; }

    /// <summary>
    /// Database application state for this migration.
    /// </summary>
    public RepositoryMigrationApplicationState State { get; init; }
}

/// <summary>
/// Application state for a migration known by the selected DbContext's current migration assembly.
/// </summary>
public enum RepositoryMigrationApplicationState
{
    /// <summary>
    /// The migration is recorded in the database history table.
    /// </summary>
    Applied,

    /// <summary>
    /// The migration is known by the current assembly but is not yet applied.
    /// </summary>
    Pending,

    /// <summary>
    /// The migration was neither reported as pending nor found in the database history table.
    /// </summary>
    Unknown
}

/// <summary>
/// Result of applying pending migrations for one Repository DbContext.
/// </summary>
public sealed record RepositoryMigrationUpdateResult
{
    /// <summary>
    /// Selected context registration metadata.
    /// </summary>
    public required RepositoryDbContextRegistration Registration { get; init; }

    /// <summary>
    /// Pending migrations detected before the update ran.
    /// </summary>
    public IReadOnlyList<string> AttemptedMigrations { get; init; } = [];

    /// <summary>
    /// Whether the update operation completed successfully.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// Whether there were no pending migrations to apply.
    /// </summary>
    public bool Skipped { get; init; }

    /// <summary>
    /// Operation duration.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// User-facing result message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Error message when the update failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
