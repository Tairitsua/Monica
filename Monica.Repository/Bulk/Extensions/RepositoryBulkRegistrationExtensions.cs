using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.Repository.Bulk.Abstractions;
using Monica.Repository.Persistence.Services;

namespace Monica.Modules;

/// <summary>Registers native row insertion on the existing Repository module.</summary>
public static class RepositoryBulkRegistrationExtensions
{
    /// <summary>
    /// Selects one singleton native insert provider for a registered repository context and its physical adapters.
    /// This enables IBulkWriter; Outbox storage remains EF-based unless UseBulkInsert is explicitly enabled.
    /// </summary>
    public static ModuleRegistration<ModuleRepository, ModuleRepositoryOption> UseBulkInsert<TDbContext, TProvider>(
        this ModuleRegistration<ModuleRepository, ModuleRepositoryOption> module)
        where TDbContext : RepositoryDbContext<TDbContext>
        where TProvider : class, IBulkInsertProvider
    {
        module.ConfigureServices(context =>
        {
            if (context.Services.Any(service => service.ServiceType == typeof(IBulkInsertProvider)
                    && service.IsKeyedService && Equals(service.ServiceKey, typeof(TDbContext))))
                throw new InvalidOperationException($"A native insert provider is already selected for '{typeof(TDbContext)}'.");
            context.Services.TryAddSingleton<TProvider>();
            context.Services.AddKeyedSingleton<IBulkInsertProvider>(typeof(TDbContext),
                (services, _) => services.GetRequiredService<TProvider>());
        });
        return module;
    }
}
