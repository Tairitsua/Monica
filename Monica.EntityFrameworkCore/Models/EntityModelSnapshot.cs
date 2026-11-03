namespace Monica.EntityFrameworkCore.Models;

/// <summary>Portable, bounded EF design-model evidence; it does not describe an inspected database.</summary>
public sealed record EntityModelSnapshot(string Schema, string Context, string Provider, string RuntimeType,
    IReadOnlyList<EntityTypeMappingSnapshot> Entities);

/// <summary>One selected entity or its owned dependent, including inherited table mappings.</summary>
public sealed record EntityTypeMappingSnapshot(string Name, bool IsOwned, string? BaseType,
    IReadOnlyList<EntityTableSnapshot> Tables, IReadOnlyList<EntityIndexSnapshot> Indexes,
    IReadOnlyList<EntityForeignKeySnapshot> ForeignKeys);

/// <summary>Relational table mapping from the supplied model, including shared-table columns.</summary>
public sealed record EntityTableSnapshot(string Name, string? Schema, IReadOnlyList<string> Key,
    IReadOnlyList<EntityColumnSnapshot> Columns);

/// <summary>One property-to-column mapping. Shadow members need no CLR property.</summary>
public sealed record EntityColumnSnapshot(string Property, string Column, string StoreType, bool Nullable,
    bool Shadow, bool ConcurrencyToken, int? MaxLength, int? Precision, int? Scale,
    string ValueGenerated, string? DefaultSql, string? ComputedSql);

/// <summary>Model index and uniqueness, expressed in property names.</summary>
public sealed record EntityIndexSnapshot(string? Name, IReadOnlyList<string> Properties, bool Unique, string? Filter);

/// <summary>Model relationship, not evidence that the database currently enforces it.</summary>
public sealed record EntityForeignKeySnapshot(IReadOnlyList<string> Properties, string PrincipalEntity,
    IReadOnlyList<string> PrincipalKey, string DeleteBehavior, bool Required);
