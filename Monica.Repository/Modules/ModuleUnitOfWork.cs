using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.Core;
using Monica.Core.Execution;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Services;
using Monica.Repository.UnitOfWork.Services.Behaviors;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>Composition helpers for operation-scoped transactions.</summary>
public static class ModuleUnitOfWorkBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Registers scoped transaction ownership in the existing execution pipeline.</summary>
        public ModuleRegistration<ModuleUnitOfWork, ModuleUnitOfWorkOption> AddUnitOfWork(Action<ModuleUnitOfWorkOption>? action = null)
            => builder.AddModule<ModuleUnitOfWork, ModuleUnitOfWorkOption>(action);
    }
}

/// <summary>Owns one transaction per operation scope and same-scope domain-event handling.</summary>
public class ModuleUnitOfWork : MonicaModule<ModuleUnitOfWorkOption>
{
    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleUnitOfWorkOption> context)
    {
        context.Services.AddSingleton(new ApplicationTransactionContexts(
            Application.TypeActivation as IApplicationModel, Option.DomainDbContexts));
        context.Services.AddScoped<UnitOfWorkManager>();
        context.Services.AddScoped<IUnitOfWorkManager>(sp => sp.GetRequiredService<UnitOfWorkManager>());
        context.Services.AddScoped<DomainEventQueue>();
        context.Services.AddScoped<IDomainEventQueue>(sp => sp.GetRequiredService<DomainEventQueue>());
    }

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<ModuleApplicationModel, ModuleApplicationModelOption>();
        module.Require<ModuleDependencyInjection, ModuleDependencyInjectionOption>();
        module.Require<ModuleExecutionPipeline, ModuleExecutionPipelineOption>(pipeline =>
            pipeline.AddBehavior(typeof(UnitOfWorkExecutionBehavior<,>), ExecutionBehaviorOrder.UnitOfWork,
                static descriptor => descriptor.TransactionMode == ExecutionTransactionMode.Automatic));
    }
}

/// <summary>Registers handlers that run inside the current transaction.</summary>
public static class ModuleUnitOfWorkRegistrationExtensions
{
    /// <summary>Registers a scoped, exact-type domain handler. It shares the publisher's DbContext.</summary>
    public static ModuleRegistration<ModuleUnitOfWork, ModuleUnitOfWorkOption> AddDomainEventHandler<TEvent, THandler>(
        this ModuleRegistration<ModuleUnitOfWork, ModuleUnitOfWorkOption> module)
        where TEvent : class
        where THandler : class, IDomainEventHandler<TEvent>
    {
        module.ConfigureServices(context => context.Services.AddScoped<IDomainEventHandler<TEvent>, THandler>());
        return module;
    }
}

/// <summary>Limits for transactional domain effects.</summary>
public class ModuleUnitOfWorkOption : ModuleOptions<ModuleUnitOfWork>
{
    /// <summary>Maximum queued domain events per operation; defaults to 1024. Exceeding it aborts possible handler cycles.</summary>
    public int MaximumDomainEvents { get; set; } = 1024;

    /// <summary>Maps persisted domains to their default write context. Empty retains standalone single-context selection.</summary>
    /// <remarks>In a complete application, an unmapped domain has no implicit persistence participant. Explicit operation context selection takes precedence.</remarks>
    public Dictionary<string, Type> DomainDbContexts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Selects a domain's primary transaction context, independently of the current AppId or active roles.</summary>
    /// <remarks>Use the existing domain enum or its exact name. Register the context through Repository as well. History and multi-context operations select their contexts explicitly.</remarks>
    public void ForDomain(object domain, Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        if (!typeof(DbContext).IsAssignableFrom(dbContextType) || dbContextType.IsAbstract || dbContextType.ContainsGenericParameters)
            throw new ArgumentException("A concrete DbContext type is required.", nameof(dbContextType));
        var id = new ApplicationDomainAttribute(domain).DomainId;
        if (!DomainDbContexts.TryAdd(id, dbContextType))
            throw new InvalidOperationException($"Domain '{id}' already has a default transaction context.");
    }
}
