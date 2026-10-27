using Microsoft.EntityFrameworkCore;
using Monica.Locker.EfCore.Internal;

namespace Monica.Locker.EfCore;

/// <summary>Maps the shared coordinator table in a participating context.</summary>
public static class LeaseModelBuilderExtensions
{
    /// <summary>Maps <c>MonicaLeases</c>; exactly one context per database/schema must own its migrations.</summary>
    /// <param name="modelBuilder">The model used by lease operations and protected transactions.</param>
    /// <param name="options">Shared schema and migration ownership; defaults to the provider schema with migration ownership.</param>
    /// <remarks>Apply migrations before workers start. This method never creates tables at runtime.</remarks>
    public static void ConfigureMonicaLeases(this ModelBuilder modelBuilder, LeaseTableOptions? options = null)
    {
        var lease = modelBuilder.Entity<LeaseRecord>();
        lease.ToTable("MonicaLeases", options?.Schema, table =>
        {
            if (options?.ExcludeFromMigrations == true) table.ExcludeFromMigrations();
        });
        lease.HasKey(row => new { row.Scope, row.Name });
        lease.Property(row => row.Scope).HasMaxLength(256);
        lease.Property(row => row.Name).HasMaxLength(512);
        lease.Property(row => row.HolderId).HasMaxLength(256);
        lease.Property(row => row.TermId).HasMaxLength(32);
        // Renewals change only non-indexed columns. Role inventories do not justify an expiry index.
    }
}

/// <summary>Configures the one shared coordinator table within a database authority.</summary>
public sealed class LeaseTableOptions
{
    /// <summary>Explicit common schema, or null for the provider/context default. All participants must resolve the same table.</summary>
    public string? Schema { get; set; }
    /// <summary>Excludes this table from this context's migrations; defaults to false. Exactly one context must own its creation.</summary>
    public bool ExcludeFromMigrations { get; set; }
}
