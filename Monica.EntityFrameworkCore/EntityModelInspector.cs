using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Monica.EntityFrameworkCore.Models;

namespace Monica.EntityFrameworkCore;

/// <summary>Reads an explicitly supplied EF relational design model without opening connections or starting a host.</summary>
public static class EntityModelInspector
{
    /// <summary>
    /// Captures the selected CLR entity and its owned dependents. Supply the design-time IModel
    /// (IDesignTimeModel.Model), exact context identity and configured provider; runtime compiled
    /// models may omit design metadata. The caller owns context creation and its side effects.
    /// </summary>
    public static EntityModelSnapshot Inspect(IModel model, Type entityType, string context, string provider)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var roots = model.GetEntityTypes().Where(entity => entity.ClrType == entityType).ToHashSet();
        if (roots.Count == 0) throw new ArgumentException("The selected CLR entity is absent from the supplied model.", nameof(entityType));
        bool Selected(IEntityType entity) => roots.Contains(entity) || entity.FindOwnership() is { } ownership && Selected(ownership.PrincipalEntityType);
        var entities = model.GetEntityTypes().Where(Selected).OrderBy(entity => entity.Name, StringComparer.Ordinal).Select(entity =>
            new EntityTypeMappingSnapshot(entity.Name, entity.IsOwned(), entity.BaseType?.Name,
                entity.GetTableMappings().Select(mapping =>
                {
                    var table = mapping.Table;
                    var store = StoreObjectIdentifier.Table(table.Name, table.Schema);
                    return new EntityTableSnapshot(table.Name, table.Schema,
                        entity.FindPrimaryKey()?.Properties.Select(property => property.Name).ToArray() ?? [],
                        mapping.ColumnMappings.Select(column => new EntityColumnSnapshot(column.Property.Name, column.Column.Name,
                            column.Column.StoreType, column.Column.IsNullable, column.Property.IsShadowProperty(), column.Property.IsConcurrencyToken,
                            column.Property.GetMaxLength(), column.Property.GetPrecision(), column.Property.GetScale(),
                            column.Property.ValueGenerated.ToString(), column.Property.GetDefaultValueSql(store),
                            column.Property.GetComputedColumnSql(store))).OrderBy(column => column.Column, StringComparer.Ordinal).ToArray());
                }).OrderBy(table => table.Schema, StringComparer.Ordinal).ThenBy(table => table.Name, StringComparer.Ordinal).ToArray(),
                entity.GetIndexes().Select(index => new EntityIndexSnapshot(index.GetDatabaseName(),
                    index.Properties.Select(property => property.Name).ToArray(), index.IsUnique, index.GetFilter())).ToArray(),
                entity.GetForeignKeys().Select(key => new EntityForeignKeySnapshot(key.Properties.Select(property => property.Name).ToArray(),
                    key.PrincipalEntityType.Name, key.PrincipalKey.Properties.Select(property => property.Name).ToArray(),
                    key.DeleteBehavior.ToString(), key.IsRequired)).ToArray())).ToArray();
        return new("monica-entity-model/v1", context, provider, entityType.FullName!, entities);
    }
}
