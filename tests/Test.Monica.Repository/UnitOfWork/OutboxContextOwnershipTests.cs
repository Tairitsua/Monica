using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Repository.UnitOfWork;

public sealed class OutboxContextOwnershipTests
{
    [Fact]
    public async Task ActiveSameDomainCallback_CannotClaimRemoteBackgroundContext()
    {
        var factory = new OwnershipFactory();
        await using var host = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var policy = host.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Equal(ActivationMode.Disabled, policy.GetActivation(factory.ContextType, TypeActivationKind.Outbox));
        Assert.Equal(ActivationMode.Automatic,
            policy.GetActivation(factory.ContextType, TypeActivationKind.Outbox, factory.RegistrationType));
        var resource = Assert.Single(host.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            item => item.Kind == TypeActivationKind.Outbox);
        Assert.Equal(factory.ContextType, resource.ImplementationType);
        Assert.Equal(factory.RegistrationType, resource.RegistrationOwner);
        Assert.Equal(ActivationMode.Disabled, resource.Mode);
        Assert.DoesNotContain(factory.Services, descriptor => descriptor.ServiceType.Name == "IOutboxStore");
        Assert.DoesNotContain(factory.Services, descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name == "OutboxWorker");
        Assert.Contains(factory.Services, descriptor => descriptor.ServiceType.IsGenericType
            && descriptor.ServiceType.GetGenericTypeDefinition().Name == "OutboxDispatcher`1");
    }

    private sealed class OwnershipFactory : MonicaTestApplicationFactory<OutboxContextOwnershipTests>
    {
        private readonly Assembly[] _assemblies;
        private readonly Type _manifestType;
        internal Type ContextType { get; }
        internal Type RegistrationType { get; }
        internal IServiceCollection Services { get; private set; } = null!;

        internal OwnershipFactory()
        {
            var domain = CreateAssembly("Domain");
            domain.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(ApplicationDomainAttribute).GetConstructor([typeof(object)])!, ["Orders"],
                [typeof(ApplicationDomainAttribute).GetProperty(nameof(ApplicationDomainAttribute.BackgroundRole))!], ["orders.delivery"]));
            var context = domain.DefineDynamicModule("Main").DefineType("OrdersContext", TypeAttributes.Public | TypeAttributes.Sealed);
            var parent = typeof(RepositoryDbContext<>).MakeGenericType(context);
            context.SetParent(parent);
            var constructor = context.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard,
                [typeof(DbContextOptions<>).MakeGenericType(context), typeof(ICachedServiceProvider)]);
            var code = constructor.GetILGenerator();
            code.Emit(OpCodes.Ldarg_0);
            code.Emit(OpCodes.Ldarg_1);
            code.Emit(OpCodes.Ldarg_2);
            var parentDefinition = typeof(RepositoryDbContext<>);
            var parentConstructor = parentDefinition.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(DbContextOptions<>).MakeGenericType(parentDefinition.GetGenericArguments()[0]), typeof(ICachedServiceProvider)],
                modifiers: null)!;
            code.Emit(OpCodes.Call, TypeBuilder.GetConstructor(parent, parentConstructor));
            code.Emit(OpCodes.Ret);
            ContextType = context.CreateType()!;

            var role = CreateAssembly("Role");
            var registration = role.DefineDynamicModule("Main").DefineType("RoleRegistration",
                TypeAttributes.Public | TypeAttributes.Sealed, typeof(OwnedOutboxRegistration));
            registration.DefineDefaultConstructor(MethodAttributes.Public);
            registration.DefineField("ContextType", typeof(Type), FieldAttributes.Public | FieldAttributes.Static);
            RegistrationType = registration.CreateType()!;
            RegistrationType.GetField("ContextType")!.SetValue(null, ContextType);
            role.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(ApplicationRoleAttribute).GetConstructor([typeof(string), typeof(object)])!, ["orders.api", "Orders"],
                [typeof(ApplicationRoleAttribute).GetProperty(nameof(ApplicationRoleAttribute.Registration))!], [RegistrationType]));

            var host = CreateAssembly("Host");
            var manifest = host.DefineDynamicModule("Main").DefineType("HostManifest", TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(OwnershipManifest));
            manifest.DefineDefaultConstructor(MethodAttributes.Public);
            manifest.DefineField("Assemblies", typeof(string[]), FieldAttributes.Public | FieldAttributes.Static);
            _manifestType = manifest.CreateType()!;
            _manifestType.GetField("Assemblies")!.SetValue(null,
                new[] { ContextType.Assembly.GetName().Name!, RegistrationType.Assembly.GetName().Name! });
            _assemblies = [ContextType.Assembly, RegistrationType.Assembly, _manifestType.Assembly];
        }

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => _assemblies;

        public new async Task<MonicaTestApplication> CreateAsync(
            Action<ISeamReplacementBuilder>? configureScenario = null, CancellationToken cancellationToken = default)
        {
            // Attribute Type arguments need resolution for only this isolated fixture's emitted assemblies.
            Assembly? ResolveFixture(object? sender, ResolveEventArgs args) =>
                _assemblies.FirstOrDefault(assembly => assembly.FullName == args.Name);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFixture;
            try { return await base.CreateAsync(configureScenario, cancellationToken); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= ResolveFixture; }
        }

        protected override void ConfigureHost(WebApplicationBuilder builder) => Services = builder.Services;

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => options.AppId = "local");
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = _manifestType;
                options.Deployments = new Dictionary<string, ApplicationPlacement>
                {
                    ["local"] = new() { Roles = ["orders.api"] },
                    ["remote"] = new() { Roles = ["orders.delivery"] }
                };
            });
        }

        private static AssemblyBuilder CreateAssembly(string kind) => AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"Test.OutboxOwnership.{kind}.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
    }

    public class OwnedOutboxRegistration : ApplicationRegistration
    {
        public override void ConfigureActive(ApplicationRegistrationContext context)
        {
            var type = (Type)GetType().GetField("ContextType")!.GetValue(null)!;
            typeof(OwnedOutboxRegistration).GetMethod(nameof(RegisterContext), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, [context.Monica]);
        }

        private static void RegisterContext<TContext>(IMonicaBuilder builder) where TContext : RepositoryDbContext<TContext>
            => builder.AddRepository().AddOutbox<TContext>();
    }

    public class OwnershipManifest : IApplicationManifest
    {
        private readonly string[] _assemblies;
        public OwnershipManifest() => _assemblies = (string[])GetType().GetField("Assemblies")!.GetValue(null)!;
        public IReadOnlyList<string> LocalAssemblies => _assemblies;
        public IReadOnlyList<ApplicationDomainDefinition> Domains => [new("Orders", _assemblies[0], "orders.delivery")];
        public IReadOnlyList<ApplicationRoleDefinition> Roles =>
        [
            new("orders.api", "Orders", _assemblies[1]),
            new("orders.delivery", "Orders", "Remote.Orders.Delivery")
        ];
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }
}
