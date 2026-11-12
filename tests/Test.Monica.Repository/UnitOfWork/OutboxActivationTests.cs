using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class OutboxActivationTests
{
    [Fact]
    public void DuplicateContext_RejectsAmbiguousStoreOwnershipBeforeStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        var failure = Assert.ThrowsAny<Exception>(() => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddRepository().AddOutbox<FirstContext>().AddOutbox<FirstContext>();
        }));
        Assert.Contains("must be registered once with one declaring owner", failure.ToString());
    }

    [Fact]
    public void OwnedStores_AreAdmittedIndependentlyBeforeSharedWorkerConstruction()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModule<OutboxAuthority, OutboxAuthorityOptions>();
        });
        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();
        var resources = application.ActivationResources.Where(resource => resource.Kind == TypeActivationKind.Outbox).ToArray();
        Assert.Equal(2, resources.Length);
        Assert.Equal(ActivationMode.Automatic, resources.Single(resource => resource.ImplementationType == typeof(FirstContext)).Mode);
        Assert.Equal(ActivationMode.Disabled, resources.Single(resource => resource.ImplementationType == typeof(SecondContext)).Mode);
        Assert.Equal(typeof(FirstOwner), resources.Single(resource => resource.ImplementationType == typeof(FirstContext)).RegistrationOwner);
        Assert.Equal(typeof(SecondOwner), resources.Single(resource => resource.ImplementationType == typeof(SecondContext)).RegistrationOwner);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType.Name == "IOutboxStore");
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name == "OutboxWorker");
        // Both passive dispatchers remain available even though only one contributes an autonomous store.
        Assert.Equal(2, builder.Services.Count(descriptor => descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition().Name == "OutboxDispatcher`1"));
    }

    private sealed class FirstOwner;
    private sealed class SecondOwner;

    public sealed class FirstContext(DbContextOptions<FirstContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<FirstContext>(options, services);
    public sealed class SecondContext(DbContextOptions<SecondContext> options, ICachedServiceProvider services)
        : RepositoryDbContext<SecondContext>(options, services);

    private sealed class ResourcePolicy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null)
        {
            if (kind != TypeActivationKind.Outbox) return ActivationMode.Automatic;
            Assert.Null(registrationOwner);
            return implementationType == typeof(SecondContext) ? ActivationMode.Disabled : ActivationMode.Automatic;
        }
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }

    private sealed class OutboxAuthority : MonicaModule<OutboxAuthorityOptions>, IApplicationCompositionModule
    {
        public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder)
            => Application.SetTypeActivationPolicy(new ResourcePolicy());
        public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder)
        {
            builder.Composition.RunRegistration(typeof(FirstOwner), () => builder.AddRepository().AddOutbox<FirstContext>());
            builder.Composition.RunRegistration(typeof(SecondOwner), () => builder.AddRepository().AddOutbox<SecondContext>());
        }
    }
    private sealed class OutboxAuthorityOptions : ModuleOptions<OutboxAuthority>;
}
