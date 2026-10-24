using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class EventBusRegistrationOwnershipTests
{
    [Theory]
    [InlineData(true, true, false, EventSubscriptionState.Active)]
    [InlineData(true, false, false, EventSubscriptionState.Held)]
    [InlineData(false, true, false, EventSubscriptionState.Disabled)]
    [InlineData(false, false, false, EventSubscriptionState.Disabled)]
    [InlineData(false, true, true, EventSubscriptionState.Disabled)]
    public async Task Discovery_WhenSharedHandlerHasRoleOwner_ShouldRetainPlacementAndIssuedIdentity(
        bool ownerLocal, bool autonomous, bool deferred, EventSubscriptionState expected)
    {
        var factory = new OwnershipFactory(ownerLocal, autonomous, deferred);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var subscription = Assert.Single(app.Services.GetRequiredService<IEventSubscriptionRegistry>()
            .GetByEventType(typeof(SharedNotice)));

        Assert.Equal(expected, subscription.State);
        Assert.Equal("issued-orders-worker", subscription.WorkloadOwner);
        Assert.Equal(0, factory.Probe.Constructions);
        var observation = Assert.Single(app.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.Kind == TypeActivationKind.ExternalSubscription
                && resource.ImplementationType == typeof(SharedHandler));
        Assert.Equal(factory.HandlerOwner, observation.RegistrationOwner);

        if (expected == EventSubscriptionState.Held)
        {
            var message = app.Services.GetRequiredService<IEventMessageFactory>()
                .Prepare(typeof(SharedNotice), new SharedNotice(), EventSubscriptionScope.Distributed);
            await app.Services.GetRequiredService<IEventReceiveDispatcher>()
                .DispatchTestAsync(message, [subscription.Id], TestContext.Current.CancellationToken);
            Assert.Equal(1, factory.Probe.Constructions);
            Assert.Equal(1, factory.Probe.Deliveries);
            Assert.Equal(EventSubscriptionState.Held, subscription.State);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Discovery_WhenSharedHandlerHasConflictingOwners_ShouldRejectBeforeFactoryConstruction(bool unowned, bool closed)
    {
        var factory = new OwnershipFactory(ownerLocal: true, autonomous: true, deferred: true,
            secondOwner: !unowned, unowned: unowned, closed: closed);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("conflicting registration owners", failure.ToString());
        Assert.Contains(closed ? typeof(SharedGenericHandler<SharedNotice>).FullName! : typeof(SharedHandler).FullName!, failure.ToString());
        Assert.Equal(0, factory.Probe.Constructions);
    }

    [Theory]
    [InlineData(true, false, false, EventSubscriptionState.Held)]
    [InlineData(true, true, true, EventSubscriptionState.Active)]
    [InlineData(false, true, false, EventSubscriptionState.Disabled)]
    public async Task Discovery_WhenClosedGenericHandlerIsRegistered_ShouldUseNativeOwnershipAndScopedDelivery(
        bool ownerLocal, bool autonomous, bool deferred, EventSubscriptionState expected)
    {
        var factory = new OwnershipFactory(ownerLocal, autonomous, deferred, closed: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var subscription = Assert.Single(app.Services.GetRequiredService<IEventSubscriptionRegistry>().GetByEventType(typeof(SharedNotice)));
        Assert.Equal(expected, subscription.State);
        Assert.Equal("issued-orders-worker", subscription.WorkloadOwner);
        Assert.Equal(0, factory.Probe.Constructions);
        var resource = Assert.Single(app.Application.ActivationResources, item => item.ImplementationType == typeof(SharedGenericHandler<SharedNotice>));
        Assert.Equal(factory.HandlerOwner, resource.RegistrationOwner);
        if (expected == EventSubscriptionState.Held)
        {
            var message = app.Services.GetRequiredService<IEventMessageFactory>()
                .Prepare(typeof(SharedNotice), new SharedNotice(), EventSubscriptionScope.Distributed);
            await app.Services.GetRequiredService<IEventReceiveDispatcher>()
                .DispatchTestAsync(message, [subscription.Id], TestContext.Current.CancellationToken);
            Assert.Equal(1, factory.Probe.Constructions);
            Assert.Equal(1, factory.Probe.Deliveries);
            Assert.Equal(EventSubscriptionState.Held, subscription.State);
        }
    }

    [Fact]
    public async Task Discovery_WhenDisabled_ShouldNotCollectClosedGenericRegistrations()
    {
        var factory = new OwnershipFactory(true, false, false, closed: true, disableDiscovery: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(app.Services.GetRequiredService<IEventSubscriptionRegistry>().GetByEventType(typeof(SharedNotice)));
        Assert.Equal(0, factory.Probe.Constructions);
    }

    private sealed class OwnershipFactory : MonicaTestApplicationFactory<SharedHandler>
    {
        private readonly Assembly[] _assemblies;
        private readonly Type _manifestType;
        private readonly bool _ownerLocal;
        private readonly bool _autonomous;
        private readonly bool _unowned;
        private readonly bool _closed;
        private readonly bool _disableDiscovery;
        internal ConstructionProbe Probe { get; } = new();
        internal Type HandlerOwner { get; }

        internal OwnershipFactory(bool ownerLocal, bool autonomous, bool deferred, bool secondOwner = false,
            bool unowned = false, bool closed = false, bool disableDiscovery = false)
        {
            _ownerLocal = ownerLocal;
            _autonomous = autonomous;
            _unowned = unowned;
            _closed = closed;
            _disableDiscovery = disableDiscovery;
            var domainAssembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"Test.EventOwner.Domain.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var domain = domainAssembly.DefineDynamicModule("Main").DefineType("DomainMarker", TypeAttributes.Public).CreateType()!;
            domainAssembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(ApplicationDomainAttribute).GetConstructor([typeof(object)])!, ["Orders"]));
            HandlerOwner = CreateRole("orders.worker", "issued-orders-worker", register: true, deferred, closed);
            var otherOwner = CreateRole("orders.api", "issued-orders-api", secondOwner, deferred, closed);
            var hostAssembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"Test.EventOwner.Host.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var manifest = hostAssembly.DefineDynamicModule("Main").DefineType("HostManifest",
                TypeAttributes.Public | TypeAttributes.Sealed, typeof(OwnershipManifest));
            manifest.DefineDefaultConstructor(MethodAttributes.Public);
            manifest.DefineField("Source", typeof(IApplicationManifest), FieldAttributes.Public | FieldAttributes.Static);
            _manifestType = manifest.CreateType()!;
            _manifestType.GetField("Source")!.SetValue(null, new Inventory(
                [new("Orders", domain.Assembly.GetName().Name!)],
                [new("orders.worker", "Orders", HandlerOwner.Assembly.GetName().Name!, "issued-orders-worker"),
                    new("orders.api", "Orders", otherOwner.Assembly.GetName().Name!, "issued-orders-api")]));
            _assemblies = [domain.Assembly, HandlerOwner.Assembly, otherOwner.Assembly,
                _manifestType.Assembly, typeof(SharedHandler).Assembly];
        }

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => _assemblies;

        public new async Task<MonicaTestApplication> CreateAsync(
            Action<ISeamReplacementBuilder>? configureScenario = null, CancellationToken cancellationToken = default)
        {
            // Reflection.Emit attribute arguments resolve by assembly name; keep resolution local to this fixture.
            Assembly? ResolveFixture(object? sender, ResolveEventArgs args) =>
                _assemblies.FirstOrDefault(assembly => assembly.FullName == args.Name);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFixture;
            try { return await base.CreateAsync(configureScenario, cancellationToken); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= ResolveFixture; }
        }

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton(Probe);
            if (_unowned) AddSharedHandler(builder.Services, _closed);
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
            builder.ConfigureApplication(options => { options.AppId = "local"; options.AutonomousWork = _autonomous; });
            builder.AddEventBus(options => options.DisableAutoDiscovery = _disableDiscovery);
            builder.AddApplicationModel(options =>
            {
                options.ManifestType = _manifestType;
                options.Deployments = new Dictionary<string, ApplicationPlacement>
                {
                    ["local"] = new() { Roles = _ownerLocal ? ["orders.worker", "orders.api"] : ["orders.api"] },
                    ["remote"] = new() { Roles = _ownerLocal ? [] : ["orders.worker"] }
                };
            });
        }

        private static Type CreateRole(string role, string workloadOwner, bool register, bool deferred, bool closed)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"Test.EventOwner.Role.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var declaration = assembly.DefineDynamicModule("Main").DefineType("OwnerRegistration",
                TypeAttributes.Public | TypeAttributes.Sealed, typeof(OwnedRegistration));
            declaration.DefineDefaultConstructor(MethodAttributes.Public);
            declaration.DefineField("RegisterHandler", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
            declaration.DefineField("Deferred", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
            declaration.DefineField("Closed", typeof(bool), FieldAttributes.Public | FieldAttributes.Static);
            var type = declaration.CreateType()!;
            type.GetField("RegisterHandler")!.SetValue(null, register);
            type.GetField("Deferred")!.SetValue(null, deferred);
            type.GetField("Closed")!.SetValue(null, closed);
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(ApplicationRoleAttribute).GetConstructor([typeof(string), typeof(object)])!, [role, "Orders"],
                [typeof(ApplicationRoleAttribute).GetProperty(nameof(ApplicationRoleAttribute.Registration))!,
                    typeof(ApplicationRoleAttribute).GetProperty(nameof(ApplicationRoleAttribute.WorkloadOwner))!],
                [type, workloadOwner]));
            return type;
        }
    }

    private static void AddSharedHandler(IServiceCollection services, bool closed)
    {
        if (closed) services.AddScoped(provider => new SharedGenericHandler<SharedNotice>(provider.GetRequiredService<ConstructionProbe>()));
        else services.AddScoped(provider => new SharedHandler(provider.GetRequiredService<ConstructionProbe>()));
    }

    public class OwnedRegistration : ApplicationRegistration
    {
        public override void ConfigurePassive(ApplicationRegistrationContext context)
        {
            if (!(bool)GetType().GetField("RegisterHandler")!.GetValue(null)!) return;
            var closed = (bool)GetType().GetField("Closed")!.GetValue(null)!;
            if ((bool)GetType().GetField("Deferred")!.GetValue(null)!)
                context.Monica.AddEventBus().ConfigureServices(module => AddSharedHandler(module.Services, closed));
            else
                AddSharedHandler(context.Services, closed);
        }
    }

    public sealed record SharedNotice;

    public sealed class SharedGenericHandler<TEvent> : IDistributedEventHandler<TEvent> where TEvent : class
    {
        private readonly ConstructionProbe _probe;
        public SharedGenericHandler(ConstructionProbe probe) { _probe = probe; probe.Constructions++; }
        public Task HandleEventAsync(TEvent eventData, CancellationToken cancellationToken)
        { _probe.Deliveries++; return Task.CompletedTask; }
    }

    public sealed class ConstructionProbe
    {
        public int Constructions;
        public int Deliveries;
    }

    public sealed class SharedHandler : IDistributedEventHandler<SharedNotice>
    {
        private readonly ConstructionProbe _probe;
        public SharedHandler(ConstructionProbe probe) { _probe = probe; probe.Constructions++; }
        public Task HandleEventAsync(SharedNotice eventData, CancellationToken cancellationToken)
        { _probe.Deliveries++; return Task.CompletedTask; }
    }

    public class OwnershipManifest : IApplicationManifest
    {
        private readonly IApplicationManifest _source;
        public OwnershipManifest() => _source = (IApplicationManifest)GetType().GetField("Source")!.GetValue(null)!;
        public IReadOnlyList<ApplicationDomainDefinition> Domains => _source.Domains;
        public IReadOnlyList<ApplicationRoleDefinition> Roles => _source.Roles;
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }

    private sealed record Inventory(IReadOnlyList<ApplicationDomainDefinition> Domains,
        IReadOnlyList<ApplicationRoleDefinition> Roles) : IApplicationManifest
    {
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }
}
