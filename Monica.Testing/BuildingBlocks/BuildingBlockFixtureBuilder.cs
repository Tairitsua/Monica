using Mapster;
using MapsterMapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Authority.Identity.Abstractions;
using Monica.Core.Mediator;
using Monica.Core.ObjectMapping.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Services;
using Monica.Testing.Doubles;
using Monica.Testing.Hosting;
using Monica.Testing.ObjectMapping;
using NSubstitute;

namespace Monica.Testing.BuildingBlocks;

/// <summary>
/// Configures a raw Microsoft DI container for fast Building Block collaboration tests.
/// </summary>
/// <typeparam name="TBuildingBlock">The Building Block implementation under test.</typeparam>
public sealed class BuildingBlockFixtureBuilder<TBuildingBlock>
    where TBuildingBlock : class
{
    private readonly IServiceCollection _services = new ServiceCollection();

    /// <summary>
    /// Initializes a builder with Monica's minimum service wiring and deterministic test seams.
    /// </summary>
    public BuildingBlockFixtureBuilder()
    {
        _services.AddOptions();
        _services.AddMonicaTestSeams();
        _services.AddScoped<IMediator, Mediator>();
        _services.AddSingleton<ICurrentUser, TestCurrentUser>();

        var mapsterConfig = new TypeAdapterConfig();
        _services.AddSingleton(mapsterConfig);
        _services.AddScoped<IMapper, ServiceMapper>();
        _services.AddTransient<IObjectMapper>(provider => new TestObjectMapper(provider.GetRequiredService<IMapper>()));
    }

    /// <summary>
    /// Applies additional service registrations to the fixture container.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_services);
        return this;
    }

    /// <summary>
    /// Registers a concrete service instance.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithInstance<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        _services.RemoveAll<TService>();
        _services.AddSingleton(instance);
        return this;
    }

    /// <summary>
    /// Registers a substitute instance for a service and returns it for inline setup.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithSubstitute<TService>(out TService substitute)
        where TService : class
    {
        substitute = Substitute.For<TService>();
        return WithInstance(substitute);
    }

    /// <summary>
    /// Registers a repository for entities without a strongly typed key.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithRepository<TEntity>(IRepository<TEntity> repository)
        where TEntity : class, IEntity
    {
        ArgumentNullException.ThrowIfNull(repository);
        _services.RemoveAll<IRepository<TEntity>>();
        _services.AddSingleton(repository);
        return this;
    }

    /// <summary>
    /// Registers a repository for entities with a strongly typed key.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithRepository<TEntity, TKey>(IRepository<TEntity, TKey> repository)
        where TEntity : class, IEntity<TKey>
    {
        ArgumentNullException.ThrowIfNull(repository);
        _services.RemoveAll<IRepository<TEntity, TKey>>();
        _services.RemoveAll<IRepository<TEntity>>();
        _services.AddSingleton(repository);
        _services.AddSingleton<IRepository<TEntity>>(repository);
        return this;
    }

    /// <summary>
    /// Replaces the current-user service used by domain services, handlers, and repositories.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithCurrentUser(ICurrentUser currentUser)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        _services.RemoveAll<ICurrentUser>();
        _services.AddSingleton(currentUser);
        return this;
    }

    /// <summary>
    /// Configures the Mapster rules used by the test object mapper.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithMappings(Action<TypeAdapterConfig> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return WithServices(services =>
        {
            services.RemoveAll<TypeAdapterConfig>();
            services.RemoveAll<IMapper>();
            var config = new TypeAdapterConfig();
            configure(config);
            services.AddSingleton(config);
            services.AddScoped<IMapper, ServiceMapper>();
        });
    }

    /// <summary>
    /// Registers a custom object mapper.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithMapper(IObjectMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        _services.RemoveAll<IObjectMapper>();
        _services.AddSingleton(mapper);
        return this;
    }

    /// <summary>
    /// Registers a scoped Building Block and initializes its Monica cached service provider when resolved.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithBuildingBlock<TService, TImplementation>()
        where TService : class
        where TImplementation : class, TService
    {
        _services.RemoveAll<TService>();
        _services.RemoveAll<TImplementation>();
        _services.AddScoped<TImplementation>(CreateCachedServiceProviderAwareInstance<TImplementation>);
        _services.AddScoped<TService>(provider => provider.GetRequiredService<TImplementation>());
        return this;
    }

    /// <summary>
    /// Registers a scoped Building Block by its concrete type and initializes its Monica cached service provider when resolved.
    /// </summary>
    public BuildingBlockFixtureBuilder<TBuildingBlock> WithBuildingBlock<TService>()
        where TService : class
    {
        _services.RemoveAll<TService>();
        _services.AddScoped(CreateCachedServiceProviderAwareInstance<TService>);
        return this;
    }

    /// <summary>
    /// Builds the raw fixture and resolves the Building Block without Monica module composition,
    /// conventional registration, dynamic proxies, or host lifecycle.
    /// </summary>
    public BuildingBlockFixture<TBuildingBlock> Build()
    {
        var rootProvider = _services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        var scope = rootProvider.CreateAsyncScope();
        try
        {
            var unit = CreateCachedServiceProviderAwareInstance<TBuildingBlock>(scope.ServiceProvider);
            var cachedServiceProvider = scope.ServiceProvider.GetRequiredService<ICachedServiceProvider>();
            return new BuildingBlockFixture<TBuildingBlock>(rootProvider, scope, unit, cachedServiceProvider);
        }
        catch
        {
            scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            rootProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static TService CreateCachedServiceProviderAwareInstance<TService>(IServiceProvider provider)
        where TService : class
    {
        var instance = ActivatorUtilities.CreateInstance<TService>(provider);

        if (instance is ICachedServiceProviderAccessor accessor)
        {
            accessor.CachedServiceProvider = provider.GetRequiredService<ICachedServiceProvider>();
        }

        return instance;
    }
}
