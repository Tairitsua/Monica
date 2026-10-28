using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Monica.EntityFrameworkCore.Extensions;
using Monica.EntityFrameworkCore.Models;
using Xunit;

[assembly: DbContextModel(typeof(Test.Monica.EntityFrameworkCore.TableLayoutTests.CompiledContext),
    typeof(Test.Monica.EntityFrameworkCore.TableLayoutTests.FlatCompiledModel))]

namespace Test.Monica.EntityFrameworkCore;

public sealed class TableLayoutTests
{
    [Theory]
    [InlineData("postgres", "infra")]
    [InlineData("gauss", "infra")]
    [InlineData("sqlite", null)]
    public void Model_WhenLayoutIsPreferred_ShouldPlaceOnlyInfrastructure(string provider, string? expectedSchema)
    {
        using var context = new LayoutContext(Options(provider).UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options);
        Assert.Null(context.Model.FindEntityType(typeof(BusinessRow))!.GetSchema());
        Assert.Equal(expectedSchema, context.Model.FindEntityType(typeof(InfrastructureRow))!.GetSchema());
        Assert.Equal(expectedSchema, context.Model.FindEntityType(typeof(OwnedDetails))!.GetSchema());
        Assert.Equal("Evidence", context.Model.FindEntityType(typeof(InfrastructureRow))!.GetTableName());
        Assert.Contains("Evidence", context.Database.GenerateCreateScript());
    }

    [Fact]
    public void Model_WhenLayoutsDiffer_ShouldKeepSeparateModelsAndReuseIdenticalLayouts()
    {
        using var first = new LayoutContext(Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options);
        using var second = new LayoutContext(Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.DefaultSchema).Options);
        using var third = new LayoutContext(Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options);
        Assert.Equal("infra", first.Model.FindEntityType(typeof(InfrastructureRow))!.GetSchema());
        Assert.Null(second.Model.FindEntityType(typeof(InfrastructureRow))!.GetSchema());
        Assert.Same(first.Model, third.Model);
        Assert.NotSame(first.Model, second.Model);
    }

    [Fact]
    public void Model_WhenLayoutIsOmitted_ShouldKeepExistingMappings()
    {
        using var context = new LayoutContext(Options("postgres").Options);
        Assert.Null(context.Model.FindEntityType(typeof(InfrastructureRow))!.GetSchema());
    }

    [Theory]
    [InlineData("postgres", "infra")]
    [InlineData("gauss", "infra")]
    [InlineData("sqlite", null)]
    public void History_WhenOptedIn_ShouldPreserveProviderAndAssembly(string provider, string? schema)
    {
        var builder = Options(provider).UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra"));
        var original = Assert.Single(builder.Options.Extensions.OfType<RelationalOptionsExtension>());
        Assert.Null(original.MigrationsHistoryTableName);
        builder.UseMonicaMigrationHistory("Flight").UseMonicaMigrationHistory("Flight");
        var configured = Assert.Single(builder.Options.Extensions.OfType<RelationalOptionsExtension>());
        Assert.Equal(original.GetType(), configured.GetType());
        Assert.Equal(original.MigrationsAssembly, configured.MigrationsAssembly);
        Assert.Equal("__EFMigrationsHistory_Flight", configured.MigrationsHistoryTableName);
        Assert.Equal(schema, configured.MigrationsHistoryTableSchema);
        using var context = new LayoutContext(builder.Options);
        var sql = context.GetService<IHistoryRepository>().GetCreateIfNotExistsScript();
        Assert.Contains("__EFMigrationsHistory_Flight", sql);
        if (schema is not null) Assert.Contains("infra", sql);
    }

    [Fact]
    public void Configure_WhenPrerequisitesOrIdentityConflict_ShouldFailBeforeDatabaseAccess()
    {
        Assert.Throws<InvalidOperationException>(() => new DbContextOptionsBuilder<LayoutContext>().UseMonicaTableLayout(InfrastructureTableLayout.DefaultSchema));
        Assert.Throws<InvalidOperationException>(() => Options("sqlite").UseMonicaMigrationHistory("Flight"));
        var builder = Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra"));
        Assert.Same(builder, builder.UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")));
        Assert.Throws<InvalidOperationException>(() => builder.UseMonicaTableLayout(InfrastructureTableLayout.DefaultSchema));
        Assert.Throws<ArgumentException>(() => InfrastructureTableLayout.PreferSchema("infra.bad"));
        Assert.Throws<ArgumentException>(() => builder.UseMonicaMigrationHistory(new string('A', 64)));
        builder.UseMonicaMigrationHistory("Flight");
        Assert.Throws<InvalidOperationException>(() => builder.UseMonicaMigrationHistory("Message"));
        var native = Options("postgres").UseNpgsql("Host=localhost;Database=unused", p => p.MigrationsHistoryTable("AnotherHistory"))
            .UseMonicaTableLayout(InfrastructureTableLayout.DefaultSchema);
        Assert.Throws<InvalidOperationException>(() => native.UseMonicaMigrationHistory("Flight"));
    }

    [Fact]
    public void Configure_WhenNativeHistoryChangesLater_ShouldRejectContextInitialization()
    {
        var builder = Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra"))
            .UseMonicaMigrationHistory("Flight")
            .UseNpgsql("Host=localhost;Database=unused", p => p.MigrationsHistoryTable("Changed"));
        using var context = new LayoutContext(builder.Options);
        Assert.Throws<InvalidOperationException>(() => context.Model);
    }

    [Fact]
    public void UseModel_WhenLayoutDiffers_ShouldRejectInsteadOfReusingWrongTables()
    {
        using var source = new LayoutContext(Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options);
        using var matching = new LayoutContext(Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).UseModel(source.Model).Options);
        Assert.Same(source.Model, matching.Model);
        using var mismatched = new LayoutContext(Options("postgres").UseMonicaTableLayout(InfrastructureTableLayout.DefaultSchema).UseModel(source.Model).Options);
        Assert.Throws<InvalidOperationException>(() => mismatched.Model);
    }

    [Fact]
    public void CompiledModel_WhenImplicitLayoutDiffers_ShouldRejectBeforeUsingIt()
    {
        using var context = new CompiledContext(new DbContextOptionsBuilder<CompiledContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options);
        Assert.Throws<InvalidOperationException>(() => context.Model);
    }

    [Fact]
    public void Model_WhenPerTableSchemaConflicts_ShouldRejectTheSecondAuthority()
    {
        using var context = new ConflictingSchemaContext(new DbContextOptionsBuilder<ConflictingSchemaContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options);
        var error = Assert.Throws<InvalidOperationException>(() => context.Model);
        Assert.Contains("conflicting with the common layout", error.Message);
    }

    [Fact]
    public async Task Save_WhenSqliteFallsBack_ShouldUseUnprefixedTablesInOneTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<LayoutContext>().UseSqlite(connection)
            .UseMonicaTableLayout(InfrastructureTableLayout.PreferSchema("infra")).Options;
        await using (var context = new LayoutContext(options))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            context.Add(new BusinessRow { Id = 1 });
            context.Add(new InfrastructureRow { Id = 1, Details = new() { Value = "staged" } });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        await using var verify = new LayoutContext(options);
        Assert.Empty(await verify.Set<BusinessRow>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await verify.Set<InfrastructureRow>().ToListAsync(TestContext.Current.CancellationToken));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('Business','Evidence')";
        Assert.Equal(2L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static DbContextOptionsBuilder<LayoutContext> Options(string provider)
    {
        var builder = new DbContextOptionsBuilder<LayoutContext>();
        return provider switch
        {
            "postgres" => builder.UseNpgsql("Host=localhost;Database=unused", p => p.MigrationsAssembly(typeof(TableLayoutTests).Assembly.FullName)),
            "gauss" => builder.UseGaussDB("Host=localhost;Database=unused", p => p.MigrationsAssembly(typeof(TableLayoutTests).Assembly.FullName)),
            _ => builder.UseSqlite("Data Source=:memory:", p => p.MigrationsAssembly(typeof(TableLayoutTests).Assembly.FullName))
        };
    }

    public sealed class LayoutContext(DbContextOptions<LayoutContext> options) : DbContext(options)
    {
        // Deliberately independent of RepositoryDbContext and its OnConfiguring override.
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BusinessRow>().ToTable("Business");
            modelBuilder.Entity<InfrastructureRow>().ToMonicaInfrastructureTable("Evidence");
            modelBuilder.Entity<InfrastructureRow>().OwnsOne(row => row.Details);
        }
    }

    public sealed class BusinessRow { public int Id { get; set; } }
    public sealed class CompiledContext(DbContextOptions<CompiledContext> options) : DbContext(options);
    public static class FlatCompiledModel
    {
        public static IModel Instance { get; } = new LayoutContext(Options("postgres")
            .UseMonicaTableLayout(InfrastructureTableLayout.DefaultSchema).Options).Model;
    }
    private sealed class ConflictingSchemaContext(DbContextOptions<ConflictingSchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BusinessRow>().ToMonicaInfrastructureTable("Conflicting").ToTable("Conflicting", "another");
        }
    }
    public sealed class InfrastructureRow
    {
        public int Id { get; set; }
        public OwnedDetails Details { get; set; } = new();
    }
    public sealed class OwnedDetails { public string Value { get; set; } = ""; }
}
