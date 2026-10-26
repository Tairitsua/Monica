using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GaussDB;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Owns one disposable database on an explicitly selected external test server.
/// </summary>
/// <remarks>
/// The configured connection must name the <c>postgres</c> maintenance database and allow database creation.
/// No application configuration is read. One unpooled admin connection and one scenario-owned pool capped at three
/// physical connections bound the resource use. EF and raw sessions share that exact data source. Cleanup disposes
/// only this pool before dropping the generated database. Missing configuration skips the tests;
/// a configured target with the wrong server product or version fails before creating any database.
/// </remarks>
internal sealed class ExternalSchedulerDatabase : IAsyncDisposable
{
    internal const string POSTGRES = "postgres";
    internal const string VASTBASE = "vastbase";
    private const string DATABASE_PREFIX = "monica_jobs_test_";
    private const int COMMAND_TIMEOUT_SECONDS = 5;
    private const int MAINTENANCE_TIMEOUT_SECONDS = 20;

    private readonly DbConnection _admin;
    private readonly DbDataSource _workloadSource;
    private bool _disposed;

    private ExternalSchedulerDatabase(
        string provider,
        DbConnection admin,
        string connectionString,
        string databaseName,
        string serverVersion)
    {
        Provider = provider;
        _admin = admin;
        _workloadSource = provider == VASTBASE
            ? GaussDBDataSource.Create(connectionString)
            : NpgsqlDataSource.Create(connectionString);
        DatabaseName = databaseName;
        ServerVersion = serverVersion;
    }

    internal string Provider { get; }
    internal string DatabaseName { get; }
    internal string ServerVersion { get; }

    internal static async Task<ExternalSchedulerDatabase> CreateAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        var variableName = provider switch
        {
            POSTGRES => "MONICA_JOBSCHEDULER_TEST_POSTGRES",
            VASTBASE => "MONICA_JOBSCHEDULER_TEST_VASTBASE",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown test provider.")
        };
        var configured = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Skip($"Set {variableName} to an isolated test server's postgres maintenance connection.");
        }

        var options = CreateOptions(provider, configured);
        Assert.Equal("postgres", options["Database"] as string);
        var admin = CreateConnection(provider, options.ConnectionString);
        ExternalSchedulerDatabase? database = null;
        try
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = admin.CreateCommand();
            command.CommandTimeout = COMMAND_TIMEOUT_SECONDS;
            // Vastbase's version() deliberately reports PostgreSQL compatibility, so it is not product proof.
            command.CommandText = provider == VASTBASE ? "SELECT pg_catalog.vb_version()" : "SELECT version()";
            var serverVersion = (string?)await command.ExecuteScalarAsync(cancellationToken)
                                ?? throw new InvalidOperationException("The database returned no server version.");
            ValidateServerVersion(provider, serverVersion);
            if (provider == POSTGRES)
            {
                command.CommandText = """
                    SELECT EXISTS (
                        SELECT 1 FROM pg_catalog.pg_proc AS procedures
                        JOIN pg_catalog.pg_namespace AS namespaces ON namespaces.oid = procedures.pronamespace
                        WHERE namespaces.nspname = 'pg_catalog'
                          AND procedures.proname IN ('vb_version', 'opengauss_version'))
                    """;
                Assert.Equal(false, await command.ExecuteScalarAsync(cancellationToken));
            }

            var databaseName = $"{DATABASE_PREFIX}{Guid.NewGuid():N}";
            // The verified Build15 instance rejects a CREATE DATABASE compatibility override.
            // Clone the empty system template, then verify the owned database before creating scheduler tables.
            command.CommandText = $"CREATE DATABASE \"{databaseName}\" TEMPLATE template0";
            command.CommandTimeout = MAINTENANCE_TIMEOUT_SECONDS;
            options["Database"] = databaseName;
            options["Pooling"] = true;
            options["Minimum Pool Size"] = 0;
            options["Maximum Pool Size"] = 3;
            database = new ExternalSchedulerDatabase(
                provider, admin, options.ConnectionString, databaseName, serverVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // Check the disposable database itself: compatibility is a database-level setting on Vastbase.
            if (provider == VASTBASE)
            {
                await using var connection = await database.OpenConnectionAsync(cancellationToken);
                await using var compatibility = connection.CreateCommand();
                compatibility.CommandTimeout = COMMAND_TIMEOUT_SECONDS;
                compatibility.CommandText = "SHOW sql_compatibility";
                Assert.Equal("PG", (string?)await compatibility.ExecuteScalarAsync(cancellationToken));
                compatibility.CommandText =
                    "SELECT datcompatibility FROM pg_catalog.pg_database WHERE datname = current_database()";
                Assert.Equal("PG", (string?)await compatibility.ExecuteScalarAsync(cancellationToken));
            }

            return database;
        }
        catch (Exception operationError)
        {
            try
            {
                if (database is not null)
                {
                    await database.DisposeAsync();
                }
                else
                {
                    await admin.DisposeAsync();
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("External database creation and its owned cleanup both failed.",
                    operationError, cleanupError);
            }

            throw;
        }
    }

    internal async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = _workloadSource.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal void ConfigureDbContext(DbContextOptionsBuilder options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Provider == VASTBASE)
        {
            options.UseGaussDB(_workloadSource);
        }
        else
        {
            options.UseNpgsql(_workloadSource);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            // The name comes only from this fixture, never from a configured connection or caller input.
            if (!Regex.IsMatch(DatabaseName, "^monica_jobs_test_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException("Refusing cleanup for a database not owned by this fixture.");
            }

            // All scenario tasks, connections and contexts are disposed by ExternalSchedulerScenario first.
            // Disposing the explicit shared data source clears only this owned database's physical pool.
            await _workloadSource.DisposeAsync();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using (var sessions = _admin.CreateCommand())
            {
                sessions.CommandTimeout = COMMAND_TIMEOUT_SECONDS;
                sessions.CommandText = """
                    SELECT pid, state, application_name FROM pg_catalog.pg_stat_activity WHERE datname = @database
                    """;
                var name = sessions.CreateParameter();
                name.ParameterName = "database";
                name.Value = DatabaseName;
                sessions.Parameters.Add(name);
                await using var reader = await sessions.ExecuteReaderAsync(cleanup.Token);
                var active = new List<string>();
                while (await reader.ReadAsync(cleanup.Token))
                {
                    active.Add($"pid={reader.GetValue(0)},state={reader.GetValue(1)},application={reader.GetValue(2)}");
                }

                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Owned cleanup {DatabaseName}: sessions=[{string.Join(";", active)}].");
            }

            await using var command = _admin.CreateCommand();
            command.CommandTimeout = MAINTENANCE_TIMEOUT_SECONDS;
            command.CommandText = $"DROP DATABASE IF EXISTS \"{DatabaseName}\"";
            var elapsed = Stopwatch.StartNew();
            try
            {
                await command.ExecuteNonQueryAsync(cleanup.Token);
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Owned cleanup {DatabaseName}: DROP completed in {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
            }
            catch (Exception exception)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Owned cleanup {DatabaseName}: DROP failed after {elapsed.Elapsed.TotalMilliseconds:F0} ms ({exception.GetType().Name}).");
                throw;
            }
        }
        finally
        {
            await _admin.DisposeAsync();
        }
    }

    private static DbConnectionStringBuilder CreateOptions(string provider, string connectionString) =>
        provider == VASTBASE
            ? new GaussDBConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Multiplexing = false,
                Enlist = false,
                Timeout = COMMAND_TIMEOUT_SECONDS,
                CommandTimeout = COMMAND_TIMEOUT_SECONDS,
                CancellationTimeout = 1000,
                ApplicationName = "Monica.JobScheduler.ExternalTests"
            }
            : new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = false,
                Multiplexing = false,
                Enlist = false,
                Timeout = COMMAND_TIMEOUT_SECONDS,
                CommandTimeout = COMMAND_TIMEOUT_SECONDS,
                CancellationTimeout = 1000,
                ApplicationName = "Monica.JobScheduler.ExternalTests"
            };

    private static DbConnection CreateConnection(string provider, string connectionString) =>
        provider == VASTBASE
            ? new GaussDBConnection(connectionString)
            : new NpgsqlConnection(connectionString);

    private static void ValidateServerVersion(string provider, string serverVersion)
    {
        if (provider == POSTGRES)
        {
            Assert.StartsWith("PostgreSQL ", serverVersion);
            Assert.False(serverVersion.Contains("Vastbase", StringComparison.OrdinalIgnoreCase));
            Assert.False(serverVersion.Contains("Gauss", StringComparison.OrdinalIgnoreCase));
            return;
        }

        Assert.True(serverVersion.Contains("Vastbase", StringComparison.OrdinalIgnoreCase),
            $"Expected the actual Vastbase server product; server reported: {serverVersion}");
        Assert.True(serverVersion.Contains("G100", StringComparison.OrdinalIgnoreCase),
            $"Expected Vastbase G100; server reported: {serverVersion}");
        Assert.Matches(new Regex(@"\bV?2\.2(?:\.15\b|\b[\s\S]*\bBuild[\s._-]*15\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), serverVersion);
    }
}
