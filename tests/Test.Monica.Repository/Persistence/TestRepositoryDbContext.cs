using Microsoft.EntityFrameworkCore;
using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Entity.Abstractions.Auditing;
using Monica.Repository.Persistence.Services;

namespace Test.Monica.Repository.Persistence;

/// <summary>
/// Fully audited soft-delete entity used by repository concept tests.
/// </summary>
public sealed class SoftDeleteAuditRow : FullAuditedEntity<Guid>, IHasConcurrencyStamp, IHasEntityVersion
{
    public SoftDeleteAuditRow() => Id = Guid.CreateVersion7();
    public string Title { get; set; } = string.Empty;
    public string ConcurrencyStamp { get; set; } = string.Empty;
    public int EntityVersion { get; set; }
    public int TenantId { get; set; } = 1;
    public RowDetail? Detail { get; set; }
}

/// <summary>
/// Entity without the soft-delete contract, used to verify hard deletes keep hard semantics.
/// </summary>
public sealed class HardDeleteRow : Entity<Guid>, IHasCreationTime
{
    public HardDeleteRow() => Id = Guid.CreateVersion7();
    public string Title { get; set; } = string.Empty;

    public DateTime CreationTime { get; set; }
    public List<RequiredChildRow> Children { get; set; } = [];
}

/// <summary>
/// Required dependent used to observe EF's orphan-deletion timing.
/// </summary>
public sealed class RequiredChildRow : Entity<Guid>
{
    public RequiredChildRow() => Id = Guid.CreateVersion7();
    public Guid ParentId { get; set; }
}

/// <summary>
/// Repository DbContext hosting the concept-test entities.
/// </summary>
public sealed class TestRepositoryDbContext(
    DbContextOptions<TestRepositoryDbContext> options,
    ICachedServiceProvider serviceProvider)
    : RepositoryDbContext<TestRepositoryDbContext>(options, serviceProvider)
{
    public DbSet<SoftDeleteAuditRow> SoftDeleteRows => Set<SoftDeleteAuditRow>();

    public DbSet<HardDeleteRow> HardDeleteRows => Set<HardDeleteRow>();
    public DbSet<GeneratedKeyRow> GeneratedRows => Set<GeneratedKeyRow>();
    public DbSet<ApplicationUuidRow> UuidRows => Set<ApplicationUuidRow>();

    protected override void OnModelCreatingExtend(ModelBuilder builder)
    {
        base.OnModelCreatingExtend(builder);
        builder.Entity<SoftDeleteAuditRow>().Property(row => row.Id).ValueGeneratedNever();
        builder.Entity<HardDeleteRow>().Property(row => row.Id).ValueGeneratedNever();
        builder.Entity<RequiredChildRow>().Property(row => row.Id).ValueGeneratedNever();
        builder.Entity<SoftDeleteAuditRow>().HasQueryFilter("Tenant", row => row.TenantId == 1);
        builder.Entity<SoftDeleteAuditRow>().OwnsOne(row => row.Detail);
        builder.Entity<HardDeleteRow>().HasMany(row => row.Children).WithOne()
            .HasForeignKey(row => row.ParentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RowDetail
{
    public string Value { get; set; } = string.Empty;
}

public sealed class GeneratedKeyRow : Entity<int>
{
    public string Title { get; set; } = string.Empty;
}

/// <summary>An application-owned UUID identity used by tracked and native persistence scenarios.</summary>
public sealed class ApplicationUuidRow : Entity<Guid>
{
    public string Title { get; set; } = string.Empty;
}
