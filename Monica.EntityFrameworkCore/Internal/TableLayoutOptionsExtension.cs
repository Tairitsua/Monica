using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.EntityFrameworkCore.Models;

namespace Monica.EntityFrameworkCore.Internal;

internal sealed class TableLayoutOptionsExtension(InfrastructureTableLayout layout, Type providerType,
    InfrastructureProviderCapabilities? capabilities, string? historyKey = null) : IDbContextOptionsExtension
{
    public InfrastructureTableLayout Layout { get; } = layout;
    public Type ProviderType { get; } = providerType;
    public InfrastructureProviderCapabilities? Capabilities { get; } = capabilities;
    public string? HistoryKey { get; } = historyKey;
    public string? Schema => Capabilities?.SupportsSchemas == true ? Layout.PreferredSchema : null;
    public object CacheIdentity => (Layout, ProviderType, Capabilities);
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services)
    {
        // EF validates extensions only on a cache miss. The actual provider is checked for every
        // context, including UseModel and reused service providers, before model/database access.
        var provider = services.LastOrDefault(s => s.ServiceType == typeof(IDatabaseProvider))
            ?? throw new InvalidOperationException("Register the relational provider before Monica table layout services.");
        services.Remove(provider);
        services.Add(ServiceDescriptor.Describe(typeof(IDatabaseProvider), serviceProvider =>
            new ValidatingDatabaseProvider((IDatabaseProvider)(provider.ImplementationInstance ??
                provider.ImplementationFactory?.Invoke(serviceProvider) ??
                ActivatorUtilities.CreateInstance(serviceProvider, provider.ImplementationType!))), provider.Lifetime));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConventionSetPlugin, TableLayoutConvention>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISingletonOptions, TableLayoutSingletonOptions>());
    }

    private sealed class ValidatingDatabaseProvider(IDatabaseProvider provider) : IDatabaseProvider
    {
        public string Name => provider.Name;
        public string? Version => provider.Version;
        public bool IsConfigured(IDbContextOptions options)
        {
            options.FindExtension<TableLayoutOptionsExtension>()?.Validate(options);
            return provider.IsConfigured(options);
        }
    }

    public void Validate(IDbContextOptions options)
    {
        var provider = TableLayoutIdentifiers.Provider(options);
        if (provider.GetType() != ProviderType)
            throw new InvalidOperationException("The database provider changed after UseMonicaTableLayout.");
        if (HistoryKey is not null && (provider.MigrationsHistoryTableName != $"__EFMigrationsHistory_{HistoryKey}" ||
                                      provider.MigrationsHistoryTableSchema != Schema))
            throw new InvalidOperationException("Migration history settings changed after UseMonicaMigrationHistory.");
        var core = options.FindExtension<CoreOptionsExtension>();
        if (core?.InternalServiceProvider is { } services &&
            !services.GetServices<ISingletonOptions>().Any(s => s is TableLayoutSingletonOptions))
            throw new InvalidOperationException("The supplied EF internal service provider lacks Monica table layout services. Apply this options extension's ApplyServices before building it, or let EF manage its internal provider.");
        if (SuppliedModel(options, core) is { } model &&
            (model.FindAnnotation(TableLayoutIdentifiers.SCHEMA_ANNOTATION)?.Value as string != (Schema ?? TableLayoutIdentifiers.DEFAULT_SCHEMA) ||
             model.FindAnnotation(TableLayoutIdentifiers.PROVIDER_ANNOTATION)?.Value as string != ProviderType.FullName))
            throw new InvalidOperationException("The supplied/compiled EF model was not built for this Monica table layout and provider.");
    }

    private static IModel? SuppliedModel(IDbContextOptions options, CoreOptionsExtension? core)
    {
        if (core?.Model is { } model) return model;
        var contextType = ((DbContextOptions)options).ContextType;
        var attribute = contextType.Assembly.GetCustomAttributes<DbContextModelAttribute>()
            .SingleOrDefault(a => a.ContextType == contextType);
        if (attribute is null) return null;
        return attribute.ModelType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IModel
            ?? throw new InvalidOperationException("The compiled EF model must expose its public static IModel Instance for layout validation.");
    }

    private sealed class ExtensionInfo(TableLayoutOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => $"MonicaTableLayout={extension.Schema ?? "default"} ";
        public override int GetServiceProviderHashCode() => extension.CacheIdentity.GetHashCode();
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo && extension.CacheIdentity.Equals(((TableLayoutOptionsExtension)other.Extension).CacheIdentity);
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Monica:TableLayout"] = extension.CacheIdentity.ToString()!;
    }
}

internal sealed class TableLayoutSingletonOptions : ISingletonOptions
{
    private object? _identity;
    public void Initialize(IDbContextOptions options) => _identity = options.FindExtension<TableLayoutOptionsExtension>()?.CacheIdentity;
    public void Validate(IDbContextOptions options)
    {
        if (!Equals(_identity, options.FindExtension<TableLayoutOptionsExtension>()?.CacheIdentity))
            throw new InvalidOperationException("One EF internal service provider cannot share different Monica table layouts. Let EF manage its internal providers or build one for each layout.");
        options.FindExtension<TableLayoutOptionsExtension>()?.Validate(options);
    }
}
