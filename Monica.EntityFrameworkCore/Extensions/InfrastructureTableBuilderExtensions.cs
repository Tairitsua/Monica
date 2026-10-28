using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Monica.EntityFrameworkCore.Internal;

namespace Monica.EntityFrameworkCore.Extensions;

/// <summary>Explicit infrastructure table declarations for module authors.</summary>
public static class InfrastructureTableBuilderExtensions
{
    /// <summary>Maps and marks one infrastructure table. Names remain module-owned; migration ownership stays explicit.</summary>
    /// <remarks>Without UseMonicaTableLayout this has normal ToTable behavior. Owned dependents sharing this table follow its schema; separately mapped owned tables must also be marked.</remarks>
    public static EntityTypeBuilder ToMonicaInfrastructureTable(this EntityTypeBuilder builder, string name,
        Action<TableBuilder>? configureTable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        builder.ToTable(name, table => configureTable?.Invoke(table));
        builder.HasAnnotation(TableLayoutIdentifiers.TABLE_ANNOTATION, name);
        return builder;
    }

    /// <summary>Typed infrastructure table declaration, preserving entity configuration chaining.</summary>
    public static EntityTypeBuilder<TEntity> ToMonicaInfrastructureTable<TEntity>(this EntityTypeBuilder<TEntity> builder,
        string name, Action<TableBuilder<TEntity>>? configureTable = null) where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        builder.ToTable(name, table => configureTable?.Invoke(table));
        builder.HasAnnotation(TableLayoutIdentifiers.TABLE_ANNOTATION, name);
        return builder;
    }

    /// <summary>Marks a separately mapped owned infrastructure table; inline dependents inherit their owning table's placement.</summary>
    public static OwnedNavigationBuilder ToMonicaInfrastructureTable(this OwnedNavigationBuilder builder, string name,
        Action<OwnedNavigationTableBuilder>? configureTable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        builder.ToTable(name, table => configureTable?.Invoke(table));
        builder.HasAnnotation(TableLayoutIdentifiers.TABLE_ANNOTATION, name);
        return builder;
    }
}
