using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Monica.EntityFrameworkCore.Internal;
using Monica.EntityFrameworkCore.Models;

namespace Monica.EntityFrameworkCore.Extensions;

/// <summary>Configures infrastructure placement independently of connection, transaction, and migration ownership.</summary>
public static class MonicaTableLayoutOptionsExtensions
{
    /// <summary>Applies a layout to marked tables. Select the relational provider first; identical repetition is harmless.</summary>
    /// <param name="builder">The context options. No database I/O is performed.</param>
    /// <param name="layout">Immutable placement policy; does not change business tables or migration history.</param>
    /// <param name="providerCapabilities">Required for unknown providers when preferring a schema or configuring history.</param>
    /// <remarks>Conflicting declarations and provider changes fail. EF-managed internal services isolate models by layout without replacing custom model cache keys.</remarks>
    public static DbContextOptionsBuilder UseMonicaTableLayout(this DbContextOptionsBuilder builder,
        InfrastructureTableLayout layout, InfrastructureProviderCapabilities? providerCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(layout);
        var provider = TableLayoutIdentifiers.Provider(builder.Options);
        var known = TableLayoutIdentifiers.Capabilities(provider);
        if (known is not null && providerCapabilities is not null && known != providerCapabilities)
            throw new InvalidOperationException("Explicit capabilities conflict with the recognized relational provider.");
        var capabilities = providerCapabilities ?? known;
        if (layout.PreferredSchema is not null && capabilities is null)
            throw new NotSupportedException($"Provider '{provider.GetType().FullName}' needs explicit InfrastructureProviderCapabilities before preferring a schema.");
        TableLayoutIdentifiers.ValidateLength(layout.PreferredSchema, capabilities);
        var extension = new TableLayoutOptionsExtension(layout, provider.GetType(), capabilities);
        var existing = builder.Options.FindExtension<TableLayoutOptionsExtension>();
        if (existing is not null)
        {
            if (!existing.CacheIdentity.Equals(extension.CacheIdentity))
                throw new InvalidOperationException("Conflicting Monica table layouts or providers were configured for this context.");
            existing.Validate(builder.Options);
            return builder;
        }
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(extension);
        return builder;
    }

    /// <summary>Typed overload preserving the context options builder.</summary>
    public static DbContextOptionsBuilder<TContext> UseMonicaTableLayout<TContext>(this DbContextOptionsBuilder<TContext> builder,
        InfrastructureTableLayout layout, InfrastructureProviderCapabilities? providerCapabilities = null) where TContext : DbContext
    {
        UseMonicaTableLayout((DbContextOptionsBuilder)builder, layout, providerCapabilities);
        return builder;
    }

    /// <summary>Uses <c>__EFMigrationsHistory_{contextKey}</c> in the resolved infrastructure namespace.</summary>
    /// <remarks>Requires provider then layout registration. The stable key belongs to a migration chain, not a role, environment, or CLR type name. Preserves the provider's history implementation and migration assembly; neither creates the schema nor transfers applied history.</remarks>
    public static DbContextOptionsBuilder UseMonicaMigrationHistory(this DbContextOptionsBuilder builder, string contextKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        TableLayoutIdentifiers.Validate(contextKey, nameof(contextKey));
        var layout = builder.Options.FindExtension<TableLayoutOptionsExtension>()
            ?? throw new InvalidOperationException("Call UseMonicaTableLayout before UseMonicaMigrationHistory.");
        layout.Validate(builder.Options);
        if (layout.Capabilities is null)
            throw new NotSupportedException("Declare provider identifier capabilities before configuring Monica migration history.");
        if (layout.HistoryKey is not null && layout.HistoryKey != contextKey)
            throw new InvalidOperationException("A context cannot own multiple Monica migration history keys.");
        var name = $"__EFMigrationsHistory_{contextKey}";
        TableLayoutIdentifiers.ValidateLength(name, layout.Capabilities);
        var provider = TableLayoutIdentifiers.Provider(builder.Options);
        if ((provider.MigrationsHistoryTableName is not null && provider.MigrationsHistoryTableName != name) ||
            (provider.MigrationsHistoryTableSchema is not null && provider.MigrationsHistoryTableSchema != layout.Schema))
            throw new InvalidOperationException("Native migration history settings conflict with UseMonicaMigrationHistory.");
        var infrastructure = (IDbContextOptionsBuilderInfrastructure)builder;
        infrastructure.AddOrUpdateExtension(provider.WithMigrationsHistoryTableName(name).WithMigrationsHistoryTableSchema(layout.Schema));
        infrastructure.AddOrUpdateExtension(new TableLayoutOptionsExtension(layout.Layout, layout.ProviderType, layout.Capabilities, contextKey));
        return builder;
    }

    /// <summary>Typed overload preserving the context options builder.</summary>
    public static DbContextOptionsBuilder<TContext> UseMonicaMigrationHistory<TContext>(this DbContextOptionsBuilder<TContext> builder,
        string contextKey) where TContext : DbContext
    {
        UseMonicaMigrationHistory((DbContextOptionsBuilder)builder, contextKey);
        return builder;
    }
}
