using Microsoft.EntityFrameworkCore;
using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Persistence.Services;

namespace ApplicationModel.Domain;

public sealed class StoredOrder
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class OrderingContext(DbContextOptions<OrderingContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<OrderingContext>(options, services)
{
    public DbSet<StoredOrder> Orders => Set<StoredOrder>();
}

public sealed class OrderingHistoryContext(DbContextOptions<OrderingHistoryContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<OrderingHistoryContext>(options, services)
{
    public DbSet<StoredOrder> Orders => Set<StoredOrder>();
}
