using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Monica.Repository.Entity.Abstractions;
using RepositoryEntity = Monica.Repository.Entity.Abstractions.Entity<long>;

namespace Monica.Repository.EntityInterfaces;

/// <summary>
/// Example entity demonstrating the implementation of IHasEntitySelfConfig interface for self-configuration.
/// </summary>
/// <remarks>
/// This is an example for documentation purposes only. This file doesn't need to be included in
/// your project. Instead, implement IHasEntitySelfConfig directly in your entity classes.
/// </remarks>
public class ExampleEntity : RepositoryEntity, IHasEntitySelfConfig<ExampleEntity>
{
    /// <summary>
    /// The entity's name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// The entity's description
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// The entity's creation date
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Entity status
    /// </summary>
    public EntityStatus Status { get; set; }

    /// <summary>
    /// Configures the entity properties and relationships.
    /// This method is automatically discovered and called by the framework.
    /// </summary>
    /// <param name="builder">The entity type builder</param>
    public void Configure(EntityTypeBuilder<ExampleEntity> builder)
    {
        // Configure table name
        builder.ToTable("Examples");
        
        // Configure primary key
        builder.HasKey(e => e.Id);
        
        // Configure properties
        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(100);
            
        builder.Property(e => e.Description)
            .HasMaxLength(500);
            
        builder.Property(e => e.CreatedAt)
            .IsRequired()
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
            
        builder.Property(e => e.Status)
            .HasConversion<string>()
            .HasMaxLength(20);
            
        // Configure indexes
        builder.HasIndex(e => e.Name);
        
        // Configure relationships
        // builder.HasMany(e => e.RelatedEntities)
        //     .WithOne(r => r.ExampleEntity)
        //     .HasForeignKey(r => r.ExampleEntityId);
    }
}

/// <summary>
/// Example enum used in the example entity
/// </summary>
public enum EntityStatus
{
    /// <summary>
    /// Draft status
    /// </summary>
    Draft,
    
    /// <summary>
    /// Active status
    /// </summary>
    Active,
    
    /// <summary>
    /// Archived status
    /// </summary>
    Archived
}
