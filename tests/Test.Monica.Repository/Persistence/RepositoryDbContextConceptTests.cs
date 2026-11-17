using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Entity.Services;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Extensions;
using Monica.Testing.Doubles;
using Monica.Testing.Repository;
using Xunit;

namespace Test.Monica.Repository.Persistence;

/// <summary>
/// Verifies that repository persistence concepts (soft delete, audit stamping) are intrinsic to the
/// DbContext save pipeline and hold when a context is saved directly, without any unit of work.
/// </summary>
public sealed class RepositoryDbContextConceptTests
{
    private const string TESTER_ID = "concept-tester";

    [Fact]
    public async Task SaveChangesAsync_WhenInsertedOutsideUnitOfWork_ShouldStampCreationAudit()
    {
        await using var fixture = await CreateFixtureAsync();
        var row = new SoftDeleteAuditRow { Title = "order-1" };

        fixture.Context.Add(row);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        row.Id.Should().NotBe(Guid.Empty);
        row.CreationTime.Should().NotBe(default);
        row.CreatorId.Should().Be(TESTER_ID);

        var stored = await fixture.Context.SoftDeleteRows.AsNoTracking()
            .SingleAsync(r => r.Title == "order-1", TestContext.Current.CancellationToken);
        stored.CreationTime.Should().NotBe(default);
        stored.CreatorId.Should().Be(TESTER_ID);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenRemovedOutsideUnitOfWork_ShouldSoftDeleteInsteadOfHardDelete()
    {
        await using var fixture = await CreateFixtureAsync();
        var row = await InsertRowAsync(fixture, "doomed");

        fixture.Context.Remove(row);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        row.IsDeleted.Should().BeTrue();
        row.DeletionTime.Should().NotBeNull();
        row.DeleterId.Should().Be(TESTER_ID);

        var visibleRows = await fixture.Context.SoftDeleteRows.AsNoTracking()
            .CountAsync(TestContext.Current.CancellationToken);
        visibleRows.Should().Be(0);

        var allRows = await fixture.Context.SoftDeleteRows.IgnoreQueryFilters().AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);
        allRows.Should().ContainSingle()
            .Which.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task SaveChangesAsync_WhenModifiedOutsideUnitOfWork_ShouldStampModificationAudit()
    {
        await using var fixture = await CreateFixtureAsync();
        var row = await InsertRowAsync(fixture, "original");

        row.Title = "renamed";
        fixture.Context.Update(row);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await fixture.Context.SoftDeleteRows.AsNoTracking()
            .SingleAsync(r => r.Id == row.Id, TestContext.Current.CancellationToken);
        stored.Title.Should().Be("renamed");
        stored.LastModificationTime.Should().NotBeNull();
        stored.LastModifierId.Should().Be(TESTER_ID);
    }

    [Fact]
    public async Task SaveChanges_WhenCalledSynchronouslyOutsideUnitOfWork_ShouldApplyConcepts()
    {
        await using var fixture = await CreateFixtureAsync();
        var row = new HardDeleteRow { Title = "sync" };

        fixture.Context.Add(row);
        fixture.Context.SaveChanges();

        row.CreationTime.Should().NotBe(default);

        fixture.Context.Remove(row);
        fixture.Context.SaveChanges();

        var remaining = await fixture.Context.HardDeleteRows.AsNoTracking()
            .CountAsync(TestContext.Current.CancellationToken);
        remaining.Should().Be(0);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenHardDeleteEntityRemovedOutsideUnitOfWork_ShouldHardDelete()
    {
        await using var fixture = await CreateFixtureAsync();
        var row = new HardDeleteRow { Title = "hard" };
        fixture.Context.Add(row);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Context.Remove(row);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var remaining = await fixture.Context.HardDeleteRows.AsNoTracking()
            .CountAsync(TestContext.Current.CancellationToken);
        remaining.Should().Be(0);
    }

    [Fact]
    public async Task Repository_WhenWrittenOutsideUnitOfWork_ShouldPersistWithConcepts()
    {
        await using var fixture = await CreateFixtureAsync();
        var repository = fixture.Repository<SoftDeleteAuditRow>();

        var inserted = new SoftDeleteAuditRow { Title = "origin" };
        repository.Add(inserted);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        inserted.CreatorId.Should().Be(TESTER_ID);

        var loaded = await repository.Query.AsTracking().SingleAsync(r => r.Title == "origin", TestContext.Current.CancellationToken);
        loaded.Title = "renamed";
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reread = await repository.Query.SingleAsync(r => r.Title == "renamed", TestContext.Current.CancellationToken);
        reread.LastModificationTime.Should().NotBeNull();

        repository.Remove(loaded);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await repository.Query.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        var softDeleted = await repository.Query.IncludeSoftDeleted().SingleAsync(TestContext.Current.CancellationToken);
        softDeleted.IsDeleted.Should().BeTrue();
        softDeleted.DeleterId.Should().Be(TESTER_ID);
    }

    private static async Task<DbContextFixture<TestRepositoryDbContext>> CreateFixtureAsync()
    {
        // Replace the identity input while retaining the production audit implementation.
        var fixture = DbContextFixture<TestRepositoryDbContext>.UseSqliteInMemory(services =>
        {
            services.AddSingleton<IAuditPropertySetter>(new AuditPropertySetter(
                new TestCurrentUser(TESTER_ID, "concept-tester"), TimeProvider.System,
                Options.Create(new ModuleClockOption())));
        });
        return await fixture.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<SoftDeleteAuditRow> InsertRowAsync(
        DbContextFixture<TestRepositoryDbContext> fixture,
        string title)
    {
        var row = new SoftDeleteAuditRow { Title = title };
        fixture.Context.Add(row);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row;
    }
}
