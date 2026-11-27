using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Modules;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Services.Support;

namespace Monica.Repository.Persistence.Services;

internal sealed class RepositoryDbContextDiagnosticsService(
    IServiceScopeFactory scopeFactory,
    IRepositoryDbContextRegistry registry,
    IOptions<ModuleRepositoryOption> repositoryOptions,
    ILogger<RepositoryDbContextDiagnosticsService> logger)
    : IRepositoryDbContextDiagnosticsService
{
    private static readonly string[] SENSITIVE_CONNECTION_KEY_FRAGMENTS =
    [
        "password",
        "pwd",
        "secret",
        "token",
        "key",
        "credential"
    ];

    public async Task<IReadOnlyList<RepositoryDbContextSnapshot>> GetRegisteredContextsAsync(
        bool revealConnectionStrings = false,
        CancellationToken cancellationToken = default)
    {
        var snapshots = new List<RepositoryDbContextSnapshot>();

        foreach (var registration in registry.GetRegistrations())
        {
            snapshots.Add(await CreateSnapshotAsync(registration, revealConnectionStrings, cancellationToken));
        }

        return snapshots;
    }

    public async Task<RepositoryActivityResult> GetActivityAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        var registration = registry.GetRequiredRegistration(contextId);
        string? providerName = null;
        RepositoryActivityProviderGuide? providerGuide = null;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = ResolveDbContext(scope.ServiceProvider, registration);
            providerName = dbContext.Database.ProviderName;
            providerGuide = RepositoryActivityProviderGuideCatalog.Resolve(providerName);

            if (!providerGuide.IsSupported)
            {
                return new RepositoryActivityResult
                {
                    Registration = registration,
                    ProviderName = providerName,
                    ProviderGuide = providerGuide,
                    Support = RepositoryActivitySupport.UnsupportedProvider,
                    Message = $"Activity diagnostics are not supported for provider '{providerName ?? "Unknown"}'."
                };
            }

            var rows = providerGuide.ProviderKind == RepositoryActivityProviderKind.OpenGauss
                ? await QueryOpenGaussActivityAsync(dbContext, cancellationToken)
                : await QueryPostgreSqlActivityAsync(dbContext, cancellationToken);

            return new RepositoryActivityResult
            {
                Registration = registration,
                ProviderName = providerName,
                ProviderGuide = providerGuide,
                Support = RepositoryActivitySupport.Supported,
                Rows = rows,
                Summary = RepositoryActivitySummary.Create(rows, dbContext.Database.GetDbConnection().Database)
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to query Repository DbContext activity for {DbContext}.", registration.FullName);
            return new RepositoryActivityResult
            {
                Registration = registration,
                ProviderName = providerName,
                ProviderGuide = providerGuide ?? RepositoryActivityProviderGuideCatalog.Resolve(providerName),
                Support = RepositoryActivitySupport.Failed,
                Message = ex.GetMessageRecursively()
            };
        }
    }

    public async Task<RepositoryMigrationStatus> GetMigrationStatusAsync(
        string contextId,
        CancellationToken cancellationToken = default)
    {
        var registration = registry.GetRequiredRegistration(contextId);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await CreateMigrationStatusAsync(
            registration, ResolveDbContext(scope.ServiceProvider, registration), cancellationToken);
    }

    public async Task<RepositoryMigrationUpdateResult> UpdateMigrationAsync(
        string contextId,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default)
    {
        var registration = registry.GetRequiredRegistration(contextId);
        return await UpdateMigrationCoreAsync(registration, commandTimeout, cancellationToken);
    }

    public async Task<IReadOnlyList<RepositoryMigrationUpdateResult>> UpdateAllPendingMigrationsAsync(
        TimeSpan commandTimeout,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RepositoryMigrationUpdateResult>();

        foreach (var registration in registry.GetRegistrations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await UpdateMigrationCoreAsync(registration, commandTimeout, cancellationToken));
        }

        return results;
    }

    private async Task<RepositoryDbContextSnapshot> CreateSnapshotAsync(
        RepositoryDbContextRegistration registration,
        bool revealConnectionString,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = ResolveDbContext(scope.ServiceProvider, registration);
            var connection = CreateConnectionInfo(dbContext, revealConnectionString);
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            var migrationStatus = await CreateMigrationStatusAsync(registration, dbContext, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return new RepositoryDbContextSnapshot
            {
                Registration = registration,
                ProviderName = dbContext.Database.ProviderName,
                MigrationAssembly = migrationStatus.MigrationAssembly,
                Connection = connection,
                CanConnect = canConnect,
                MigrationCount = migrationStatus.HasError ? null : migrationStatus.AllMigrations.Count,
                PendingMigrationCount = migrationStatus.HasError ? null : migrationStatus.PendingMigrations.Count,
                MigrationStatus = migrationStatus,
                Options = CreateOptionEntries(registration, dbContext)
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to build Repository DbContext snapshot for {DbContext}.", registration.FullName);
            return new RepositoryDbContextSnapshot
            {
                Registration = registration,
                ErrorMessage = ex.GetMessageRecursively()
            };
        }
    }

    private async Task<RepositoryMigrationStatus> CreateMigrationStatusAsync(
        RepositoryDbContextRegistration registration,
        DbContext dbContext,
        CancellationToken cancellationToken)
    {
        var status = new RepositoryMigrationStatus
        {
            Registration = registration,
            ProviderName = dbContext.Database.ProviderName
        };
        try
        {
            status = status with
            {
                MigrationAssembly = GetMigrationAssemblyName(dbContext),
                AllMigrations = dbContext.Database.GetMigrations().ToArray()
            };
            var applied = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            return status with
            {
                AppliedMigrations = applied,
                PendingMigrations = status.AllMigrations.Except(applied, StringComparer.Ordinal).ToArray()
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to read Repository migration status for {DbContext}.", registration.FullName);
            return status with { ErrorMessage = ex.GetMessageRecursively() };
        }
    }

    private async Task<RepositoryMigrationUpdateResult> UpdateMigrationCoreAsync(
        RepositoryDbContextRegistration registration,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        List<string> pendingMigrations = [];

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = ResolveDbContext(scope.ServiceProvider, registration);
            var previousTimeout = dbContext.Database.GetCommandTimeout();

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(commandTimeout);

            try
            {
                dbContext.Database.SetCommandTimeout(commandTimeout);
                pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(timeoutSource.Token)).ToList();

                if (pendingMigrations.Count == 0)
                {
                    return new RepositoryMigrationUpdateResult
                    {
                        Registration = registration,
                        Succeeded = true,
                        Skipped = true,
                        Duration = stopwatch.Elapsed,
                        Message = "No pending migrations."
                    };
                }

                // The provider migrator owns its transactions and may issue commands outside them.
                await dbContext.Database.MigrateAsync(timeoutSource.Token);

                return new RepositoryMigrationUpdateResult
                {
                    Registration = registration,
                    AttemptedMigrations = pendingMigrations,
                    Succeeded = true,
                    Duration = stopwatch.Elapsed,
                    Message = "Migration update completed."
                };
            }
            finally
            {
                dbContext.Database.SetCommandTimeout(previousTimeout);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update Repository DbContext migrations for {DbContext}.", registration.FullName);
            return new RepositoryMigrationUpdateResult
            {
                Registration = registration,
                AttemptedMigrations = pendingMigrations,
                Succeeded = false,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.GetMessageRecursively()
            };
        }
    }

    private static DbContext ResolveDbContext(IServiceProvider serviceProvider, RepositoryDbContextRegistration registration)
    {
        return (DbContext)serviceProvider.GetRequiredService(registration.DbContextType);
    }

    private static RepositoryConnectionInfo CreateConnectionInfo(DbContext dbContext, bool revealConnectionString)
    {
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var connectionString = connection.ConnectionString;
            return new RepositoryConnectionInfo
            {
                DataSource = connection.DataSource,
                Database = connection.Database,
                Pool = RepositoryConnectionPoolInfoParser.Parse(connectionString),
                MaskedConnectionString = MaskConnectionString(connectionString),
                RevealedConnectionString = revealConnectionString ? connectionString : null
            };
        }
        catch
        {
            return new RepositoryConnectionInfo();
        }
    }

    private static string? MaskConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            foreach (var key in builder.Keys.Cast<string>().ToList())
            {
                if (IsSensitiveConnectionKey(key))
                {
                    builder[key] = "******";
                }
            }

            return builder.ConnectionString;
        }
        catch
        {
            return "******";
        }
    }

    private static bool IsSensitiveConnectionKey(string key)
    {
        return SENSITIVE_CONNECTION_KEY_FRAGMENTS.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<RepositoryOptionEntry> CreateOptionEntries(
        RepositoryDbContextRegistration registration,
        DbContext dbContext)
    {
        var moduleOption = repositoryOptions.Value;
        var entries = new List<RepositoryOptionEntry>
        {
            CreateOptionEntry("Registration.ProviderType", "Provider type", registration.ProviderType),
            CreateOptionEntry("Registration.AssemblyName", "Assembly", registration.AssemblyName),
            CreateOptionEntry("DbContext.QueryTrackingBehavior", "Query tracking", dbContext.ChangeTracker.QueryTrackingBehavior),
            CreateOptionEntry("ModuleRepository.UseDbFunction", "Use DbFunction", moduleOption.UseDbFunction),
            CreateOptionEntry("ModuleRepository.EnableSensitiveDataLogging", "Sensitive data logging", moduleOption.EnableSensitiveDataLogging?.ToString() ?? "Environment default"),
            CreateOptionEntry("ModuleRepository.EnableEfCoreConnectionMetrics", "EF Core connection metrics", moduleOption.EnableEfCoreConnectionMetrics),
            CreateOptionEntry("ModuleRepository.DisableEntitySelfConfiguration", "Disable entity self configuration", moduleOption.DisableEntitySelfConfiguration),
            CreateOptionEntry("ModuleRepository.DisableEntitySeparateConfiguration", "Disable entity separate configuration", moduleOption.DisableEntitySeparateConfiguration)
        };
        return entries;

        RepositoryOptionEntry CreateOptionEntry(string path, string label, object? value)
        {
            return new RepositoryOptionEntry
            {
                Path = path,
                Label = label,
                Value = FormatOptionValue(value)
            };
        }
    }

    private static string? GetMigrationAssemblyName(DbContext dbContext)
    {
        try
        {
            return dbContext.GetService<IMigrationsAssembly>().Assembly.GetName().Name;
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<RepositoryActivityRow>> QueryPostgreSqlActivityAsync(
        DbContext dbContext,
        CancellationToken cancellationToken)
    {
        return await QueryActivityRowsAsync(
            dbContext,
            PostgreSqlActivityQuery.GetSql(),
            row => new RepositoryActivityRow
            {
                Runtime = row.GetTimeSpan("Runtime"),
                DatabaseName = row.GetString("Datname"),
                BackendId = row.GetString("Pid"),
                UserName = row.GetString("Usename"),
                ApplicationName = row.GetString("ApplicationName"),
                ClientAddress = row.GetString("ClientAddr"),
                ClientHostname = row.GetString("ClientHostname"),
                ClientPort = row.GetInt32("ClientPort"),
                BackendStart = row.GetDateTime("BackendStart"),
                TransactionStart = row.GetDateTime("XactStart"),
                QueryStart = row.GetDateTime("QueryStart"),
                StateChange = row.GetDateTime("StateChange"),
                Waiting = row.GetBoolean("Waiting"),
                State = row.GetString("State"),
                Query = row.GetString("Query")
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<RepositoryActivityRow>> QueryOpenGaussActivityAsync(
        DbContext dbContext,
        CancellationToken cancellationToken)
    {
        return await QueryActivityRowsAsync(
            dbContext,
            OpenGaussActivityQuery.GetSql(),
            row => new RepositoryActivityRow
            {
                Runtime = row.GetTimeSpan("Runtime"),
                DatabaseName = row.GetString("Datname"),
                BackendId = row.GetString("Sessionid") ?? row.GetString("Pid"),
                UserName = row.GetString("Usename"),
                ApplicationName = row.GetString("ApplicationName"),
                ClientAddress = row.GetString("ClientAddr"),
                ClientHostname = row.GetString("ClientHostname"),
                ClientPort = row.GetInt32("ClientPort"),
                BackendStart = row.GetDateTime("BackendStart"),
                TransactionStart = row.GetDateTime("XactStart"),
                QueryStart = row.GetDateTime("QueryStart"),
                StateChange = row.GetDateTime("StateChange"),
                Waiting = row.GetBoolean("Waiting"),
                State = row.GetString("State"),
                Query = row.GetString("Query"),
                Detail = row.GetString("ResourcePool")
            },
            cancellationToken);
    }

    private static async Task<IReadOnlyList<RepositoryActivityRow>> QueryActivityRowsAsync(
        DbContext dbContext,
        string sql,
        Func<ActivityRowReader, RepositoryActivityRow> mapRow,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != ConnectionState.Open;

        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();

            var commandTimeout = dbContext.Database.GetCommandTimeout();
            if (commandTimeout is not null)
            {
                command.CommandTimeout = commandTimeout.Value;
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rowReader = new ActivityRowReader(reader);
            var rows = new List<RepositoryActivityRow>();

            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(mapRow(rowReader));
            }

            return rows
                .OrderByDescending(row => row.Runtime ?? TimeSpan.MinValue)
                .ToList();
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string FormatOptionValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            bool flag => flag ? "Enabled" : "Disabled",
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            Enum enumValue => enumValue.ToString(),
            DateTime dateTime => dateTime.ToString("u", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("u", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable enumerable and not string => string.Join(", ", enumerable.Cast<object?>().Take(8).Select(FormatOptionValue)),
            _ => value.ToString() ?? string.Empty
        };
    }

    private sealed class ActivityRowReader
    {
        private readonly DbDataReader reader;
        private readonly Dictionary<string, int> ordinals;

        public ActivityRowReader(DbDataReader reader)
        {
            this.reader = reader;
            ordinals = Enumerable
                .Range(0, reader.FieldCount)
                .ToDictionary(reader.GetName, index => index, StringComparer.OrdinalIgnoreCase);
        }

        public string? GetString(string name)
        {
            return FormatProviderValue(GetValue(name));
        }

        public int? GetInt32(string name)
        {
            return ConvertToInt64(GetValue(name)) is { } value
                && value >= int.MinValue
                && value <= int.MaxValue
                    ? (int)value
                    : null;
        }

        public bool? GetBoolean(string name)
        {
            var value = GetValue(name);
            return value switch
            {
                null => null,
                bool flag => flag,
                string text when bool.TryParse(text, out var flag) => flag,
                string text when string.Equals(text, "t", StringComparison.OrdinalIgnoreCase) => true,
                string text when string.Equals(text, "f", StringComparison.OrdinalIgnoreCase) => false,
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number != 0,
                IConvertible => ConvertToInt64(value) is { } number && number != 0,
                _ => null
            };
        }

        public DateTime? GetDateTime(string name)
        {
            var value = GetValue(name);
            return value switch
            {
                null => null,
                DateTime dateTime => dateTime,
                DateTimeOffset dateTimeOffset => dateTimeOffset.DateTime,
                string text when DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var dateTime) => dateTime,
                _ => null
            };
        }

        public TimeSpan? GetTimeSpan(string name)
        {
            var value = GetValue(name);
            return value switch
            {
                null => null,
                TimeSpan timeSpan => timeSpan,
                string text when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeSpan) => timeSpan,
                _ => null
            };
        }

        private object? GetValue(string name)
        {
            if (!ordinals.TryGetValue(name, out var ordinal))
            {
                return null;
            }

            var value = reader.GetValue(ordinal);
            return value is DBNull ? null : value;
        }

        private static string? FormatProviderValue(object? value)
        {
            return value switch
            {
                null => null,
                string text => text,
                char[] chars => new string(chars),
                byte[] bytes => Convert.ToHexString(bytes),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };
        }

        private static long? ConvertToInt64(object? value)
        {
            return value switch
            {
                null => null,
                byte number => number,
                sbyte number => number,
                short number => number,
                ushort number => number,
                int number => number,
                uint number => number,
                long number => number,
                ulong number when number <= long.MaxValue => (long)number,
                decimal number when number >= long.MinValue && number <= long.MaxValue => decimal.ToInt64(number),
                double number when number >= long.MinValue && number <= long.MaxValue => Convert.ToInt64(number, CultureInfo.InvariantCulture),
                float number when number >= long.MinValue && number <= long.MaxValue => Convert.ToInt64(number, CultureInfo.InvariantCulture),
                string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
                IConvertible convertible => TryConvertToInt64(convertible),
                _ => null
            };
        }

        private static long? TryConvertToInt64(IConvertible convertible)
        {
            try
            {
                return convertible.ToInt64(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return null;
            }
        }
    }
}
