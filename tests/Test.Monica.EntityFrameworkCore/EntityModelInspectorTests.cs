using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Monica.EntityFrameworkCore;
using Xunit;

namespace Test.Monica.EntityFrameworkCore;

public sealed class EntityModelInspectorTests
{
    [Fact]
    public void Inspect_WhenMappingHasInheritanceOwnedAndShadowMembers_ShouldKeepPhysicalFactsWithoutOpeningDatabase()
    {
        using var context = new ModelContext(new DbContextOptionsBuilder<ModelContext>().UseSqlite("Data Source=:memory:").Options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var observed = EntityModelInspector.Inspect(model, typeof(Order), nameof(ModelContext), context.Database.ProviderName!);
        Assert.Equal("monica-entity-model/v1", observed.Schema);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        var order = Assert.Single(observed.Entities, entity => entity.Name == typeof(Order).FullName);
        Assert.Contains(order.Tables, table => table.Name == "Orders");
        Assert.Contains(order.Tables, table => table.Name == "Records");
        var columns = order.Tables.SelectMany(table => table.Columns).ToArray();
        Assert.Contains(columns, column => column.Property == "Revision" && column.Shadow && column.ConcurrencyToken);
        Assert.Contains(columns, column => column.Property == "Reference" && column.MaxLength == 64 && !column.Nullable);
        Assert.Contains(order.Indexes, index => index.Unique && index.Properties.Contains("Reference"));
        Assert.Contains(observed.Entities, entity => entity.IsOwned && entity.Tables.Any(table => table.Name == "OrderDetails"));
        Assert.Throws<ArgumentException>(() => EntityModelInspector.Inspect(model, typeof(string), nameof(ModelContext), "sqlite"));
    }

    private class Record { public long Id { get; set; } }
    private sealed class Order : Record
    {
        public string Reference { get; set; } = string.Empty;
        public Details Details { get; set; } = new();
    }
    private sealed class Details { public string? Note { get; set; } }
    private sealed class ModelContext(DbContextOptions<ModelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Record>().ToTable("Records");
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("Orders");
                entity.Property(order => order.Reference).HasMaxLength(64).IsRequired();
                entity.HasIndex(order => order.Reference).IsUnique();
                entity.Property<int>("Revision").IsConcurrencyToken();
                entity.OwnsOne(order => order.Details, details => details.ToTable("OrderDetails"));
            });
        }
    }
}
