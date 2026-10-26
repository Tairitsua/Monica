using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Extensions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.EfCore;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Creates the production scheduler store over an owned external database, with fresh contexts per operation.
/// </summary>
internal sealed class ExternalSchedulerStoreFixture : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly IDbContextFactory<JobSchedulerDbContext> _factory;

    private ExternalSchedulerStoreFixture(
        ExternalSchedulerDatabase database,
        IHost host,
        IDbContextFactory<JobSchedulerDbContext> factory)
    {
        Database = database;
        _host = host;
        _factory = factory;
    }

    internal ExternalSchedulerDatabase Database { get; }

    internal static async Task<ExternalSchedulerStoreFixture> CreateAsync(
        string provider,
        CancellationToken cancellationToken,
        params IInterceptor[] interceptors)
    {
        var database = await ExternalSchedulerDatabase.CreateAsync(provider, cancellationToken);
        IHost? host = null;
        try
        {
            var builder = Host.CreateApplicationBuilder();
            builder.AddMonica(monica => monica.AddRepository()
                .AddRepositoryDbContext<JobSchedulerDbContext>(
                    (_, options) =>
                    {
                        database.ConfigureDbContext(options);
                        options.AddInterceptors(interceptors);
                    }, DbContextProviderType.Default));
            builder.Services.AddMonicaTestSeams();
            host = builder.Build();
            var factory = host.Services.GetRequiredService<IDbContextFactory<JobSchedulerDbContext>>();
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            await using (var connection = await database.OpenConnectionAsync(cancellationToken))
            {
                await using var emptySchema = connection.CreateCommand();
                emptySchema.CommandTimeout = 5;
                emptySchema.CommandText = """
                    SELECT relations.relkind::text || ':' || relations.relname::text
                    FROM pg_catalog.pg_class AS relations
                    JOIN pg_catalog.pg_namespace AS schemas ON schemas.oid = relations.relnamespace
                    WHERE schemas.nspname = 'public' AND relations.relkind IN ('r', 'v', 'm', 'f', 'p')
                    ORDER BY relations.relname
                    """;
                await using var reader = await emptySchema.ExecuteReaderAsync(cancellationToken);
                var observed = new List<string>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    observed.Add(reader.GetString(0));
                }

                // This verified Build15 template also carries its login-information view in public.
                // Require the exact vendor baseline so an unexpected application relation cannot be ignored.
                string[] baseline = provider == ExternalSchedulerDatabase.VASTBASE ? ["v:vb_login_info"] : [];
                Assert.Equal(baseline, observed);
            }

            // Vastbase's empty template contains vendor objects. EnsureCreated sees those objects and skips the
            // scheduler model, so create only this model after verifying the fixture-owned template baseline.
            TestContext.Current.AddAttachment("scheduler-schema.sql", context.Database.GenerateCreateScript());
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine($"Created scheduler model tables in owned {database.DatabaseName}.");
            return new ExternalSchedulerStoreFixture(database, host, factory);
        }
        catch (Exception operationError)
        {
            try
            {
                try
                {
                    host?.Dispose();
                }
                finally
                {
                    await database.DisposeAsync();
                }
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("External scheduler schema creation and its owned cleanup both failed.",
                    operationError, cleanupError);
            }

            throw;
        }
    }

    /// <summary>
    /// Creates a distinct store whose concurrent operations borrow separate connections from the one owned pool.
    /// </summary>
    internal IJobSchedulerStore CreateStore() => new EfCoreJobSchedulerStore(_factory);

    public async ValueTask DisposeAsync()
    {
        try
        {
            _host.Dispose();
        }
        finally
        {
            await Database.DisposeAsync();
        }
    }
}
