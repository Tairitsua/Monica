using System.Text;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Monica.EntityFrameworkCore.Models;

namespace Monica.EntityFrameworkCore.Internal;

internal static class TableLayoutIdentifiers
{
    public const string TABLE_ANNOTATION = "Monica:InfrastructureTable";
    public const string SCHEMA_ANNOTATION = "Monica:InfrastructureSchema";
    public const string PROVIDER_ANNOTATION = "Monica:InfrastructureProvider";
    public const string DEFAULT_SCHEMA = "<default>";

    public static void Validate(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (!(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Use a portable identifier beginning with a letter or underscore and containing only ASCII letters, digits, or underscores.", parameter);
    }

    public static void ValidateLength(string? value, InfrastructureProviderCapabilities? capabilities)
    {
        if (value is not null && capabilities is not null && Encoding.UTF8.GetByteCount(value) > capabilities.MaxIdentifierLength)
            throw new ArgumentException($"Identifier '{value}' exceeds the provider limit of {capabilities.MaxIdentifierLength} UTF-8 bytes.");
    }

    public static RelationalOptionsExtension Provider(IDbContextOptions options)
    {
        var providers = options.Extensions.Where(e => e.Info.IsDatabaseProvider).ToArray();
        return providers.Length == 1 && providers[0] is RelationalOptionsExtension relational
            ? relational
            : throw new InvalidOperationException("Select exactly one relational provider before UseMonicaTableLayout.");
    }

    public static InfrastructureProviderCapabilities? Capabilities(RelationalOptionsExtension provider) => provider.GetType().FullName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.NpgsqlOptionsExtension" => new(true, 63),
        "GaussDB.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal.GaussDBOptionsExtension" => new(true, 63),
        "Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal.SqliteOptionsExtension" => new(false, short.MaxValue),
        _ => null
    };
}
