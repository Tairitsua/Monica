using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Monica.Repository.Bulk.Abstractions;

namespace Test.Monica.Repository.Bulk;

/// <summary>A raw SQL boundary double; proves transaction ownership without pretending to test GaussDB COPY.</summary>
public sealed class SqliteNativeInsertProvider : IBulkInsertProvider
{
    public List<Type> InsertedTypes { get; } = [];
    public int SynchronousCalls { get; private set; }
    public Action<Type>? AfterInsert { get; set; }

    public void ValidateInsert<TRow>(DbContext context, IReadOnlyList<TRow> rows) where TRow : class
    {
        if (!context.Database.IsSqlite()) throw new NotSupportedException("Test provider requires SQLite.");
    }

    public void Insert<TRow>(DbContext context, IReadOnlyList<TRow> rows, CancellationToken cancellationToken) where TRow : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = CreateCommand(context, rows);
        command.ExecuteNonQuery();
        SynchronousCalls++;
        Completed<TRow>();
    }

    public async Task InsertAsync<TRow>(DbContext context, IReadOnlyList<TRow> rows, CancellationToken cancellationToken) where TRow : class
    {
        await using var command = CreateCommand(context, rows);
        await command.ExecuteNonQueryAsync(cancellationToken);
        Completed<TRow>();
    }

    private void Completed<TRow>()
    {
        InsertedTypes.Add(typeof(TRow));
        AfterInsert?.Invoke(typeof(TRow));
    }

    private static DbCommand CreateCommand<TRow>(DbContext context, IReadOnlyList<TRow> rows) where TRow : class
    {
        var entity = context.Model.FindEntityType(typeof(TRow))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var properties = entity.GetProperties().Where(property => !property.IsShadowProperty()
            && !(property.IsPrimaryKey() && property.ValueGenerated != ValueGenerated.Never)).ToArray();
        var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        var values = new List<string>();
        foreach (var row in rows)
        {
            var parameters = new List<string>();
            foreach (var property in properties)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = $"@p{command.Parameters.Count}";
                var value = property.PropertyInfo!.GetValue(row);
                parameter.Value = (property.GetTypeMapping().Converter is { } converter
                    ? converter.ConvertToProvider(value) : value) ?? DBNull.Value;
                command.Parameters.Add(parameter);
                parameters.Add(parameter.ParameterName);
            }
            values.Add($"({string.Join(',', parameters)})");
        }
        command.CommandText = $"INSERT INTO {Quote(table.Name)} "
            + $"({string.Join(',', properties.Select(property => Quote(property.GetColumnName(table)!)))}) "
            + $"VALUES {string.Join(',', values)}";
        return command;
    }

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
