using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Core.Clock;
using Monica.Modules;
using Monica.Testing.Doubles;
using Monica.Testing.Repository;
using Xunit;

namespace Test.Monica.Repository.Persistence;

/// <summary>
/// Verifies audit timestamp rendering against the Clock module's deployment-timezone option:
/// UTC by default, an explicitly configured deployment timezone otherwise, and never the host's
/// operating-system timezone.
/// </summary>
public sealed class AuditTimeZoneTests
{
    [Fact]
    public async Task SaveChangesAsync_WithoutConfiguredTimeZone_ShouldStampUtc()
    {
        await using var fixture = await CreateFixtureAsync(null);
        var before = DateTime.UtcNow;

        var row = await InsertRowAsync(fixture, "utc-stamp");

        var after = DateTime.UtcNow;
        row.CreationTime.Should().BeOnOrAfter(before);
        row.CreationTime.Should().BeOnOrBefore(after);
    }

    [Fact]
    public async Task SaveChangesAsync_WithConfiguredDeploymentTimeZone_ShouldStampConvertedWallClock()
    {
        var zone = CommonTimeZone.China.GetTimeZoneInfo();
        await using var fixture = await CreateFixtureAsync(CommonTimeZone.China);
        var before = DateTime.UtcNow;

        var row = await InsertRowAsync(fixture, "china-stamp");

        var after = DateTime.UtcNow;
        var offset = zone.GetUtcOffset(DateTime.UtcNow);
        row.CreationTime.Should().BeOnOrAfter(before + offset);
        row.CreationTime.Should().BeOnOrBefore(after + offset);
    }

    private static async Task<DbContextFixture<TestRepositoryDbContext>> CreateFixtureAsync(CommonTimeZone? localTimeZone)
    {
        // The audit setter comes from the fixture's service provider; the clock option registered
        // here is the only timezone input, so the stamps are independent of this host's timezone.
        var fixture = DbContextFixture<TestRepositoryDbContext>.UseSqliteInMemory(services =>
        {
            services.AddSingleton<IOptions<ModuleClockOption>>(
                Options.Create(new ModuleClockOption { LocalTimeZone = localTimeZone }));
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
