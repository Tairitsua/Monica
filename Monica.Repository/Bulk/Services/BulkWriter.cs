using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Monica.Repository.Bulk.Abstractions;
using Monica.Repository.Bulk.Models;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Services.Support;

namespace Monica.Repository.Bulk.Services;

internal sealed class BulkWriter : IBulkWriter
{
    public BulkInsertOperation<TRow> Insert<TRow>(DbContext context, IReadOnlyCollection<TRow> rows) where TRow : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        return new(context, rows);
    }

    internal static async Task ExecuteAsync<TRow>(DbContext context, IReadOnlyCollection<TRow> input,
        bool audit, bool useAsync, CancellationToken token) where TRow : class
    {
        token.ThrowIfCancellationRequested();
        if (input.Count == 0) return;
        if (context is not IRepositoryContextLifetime lifetime)
            throw new NotSupportedException("Native inserts require a Monica RepositoryDbContext.");
        if (context is IRepositoryContextAdapter { CoordinatesSave: true })
            throw new NotSupportedException("Select a physical shard context before executing a native insert.");

        lifetime.ValidateDatabaseAccess();
        var provider = lifetime.Services.GetKeyedService<IBulkInsertProvider>(lifetime.RegistrationType)
            ?? throw new InvalidOperationException($"Register a native insert provider for '{lifetime.RegistrationType}'.");
        var rows = input.ToArray();
        ValidateRows(context, rows);
        lifetime.PrepareNativeWrite([typeof(TRow)]);
        if (audit)
        {
            var setter = lifetime.Services.GetRequiredService<IAuditPropertySetter>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                setter.SetCreationProperties(row);
            }
        }
        // Audit can fill a previously missing generated/default-valued property. Validate the final row shape.
        provider.ValidateInsert(context, rows);
        if (useAsync) await provider.InsertAsync(context, rows, token);
        else provider.Insert(context, rows, token);
        token.ThrowIfCancellationRequested();
    }

    private static void ValidateRows<TRow>(DbContext context, IReadOnlyList<TRow> rows) where TRow : class
    {
        var entity = context.Model.FindEntityType(typeof(TRow))
            ?? throw new NotSupportedException($"'{typeof(TRow)}' is not a mapped row type.");
        var key = entity.FindPrimaryKey()
            ?? throw new NotSupportedException("Native inserts require a mapped primary key.");
        if (key.Properties.Any(property => property.PropertyInfo is null && property.FieldInfo is null))
            throw new NotSupportedException("Native inserts do not support shadow primary keys.");
        var keys = new HashSet<object?[]>(new KeyComparer(key.Properties));
        foreach (var row in rows)
        {
            if (row is null || row.GetType() != typeof(TRow))
                throw new ArgumentException("Every row must be non-null and have the exact declared mapped type.", nameof(rows));
            var values = key.Properties.Select(property => property.PropertyInfo is { } info
                ? info.GetValue(row) : property.FieldInfo!.GetValue(row)).ToArray();
            // Database-generated keys are absent from the insert and cannot identify a tracked/input conflict.
            if (key.Properties.Any(property => property.ValueGenerated != ValueGenerated.Never)) continue;
            if (!keys.Add(values)) throw new ArgumentException("Native insert input contains duplicate primary keys.", nameof(rows));
        }
        context.ChangeTracker.DetectChanges();
        foreach (var entry in context.ChangeTracker.Entries().Where(entry => entry.Metadata == entity))
        {
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                || keys.Contains(key.Properties.Select(property => entry.Property(property.Name).CurrentValue).ToArray()))
                throw new InvalidOperationException("Native insert conflicts with tracked rows. Complete the tracked work or use a fresh operation.");
        }
    }

    private sealed class KeyComparer(IReadOnlyList<IProperty> properties) : IEqualityComparer<object?[]>
    {
        public bool Equals(object?[]? left, object?[]? right) => left is not null && right is not null
            && properties.Select((property, index) => property.GetKeyValueComparer().Equals(left[index], right[index])).All(equal => equal);

        public int GetHashCode(object?[] values)
        {
            var hash = new HashCode();
            for (var index = 0; index < properties.Count; index++)
                hash.Add(values[index] is { } value ? properties[index].GetKeyValueComparer().GetHashCode(value) : 0);
            return hash.ToHashCode();
        }
    }
}
