using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.ApplicationModel.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ApplicationModel;

public sealed class ApplicationRegistrationTests
{
    [Theory]
    [InlineData("api", true, ActivationMode.Disabled)]
    [InlineData("worker", true, ActivationMode.Automatic)]
    [InlineData("worker", false, ActivationMode.Manual)]
    public async Task DomainResources_ShouldUseBackgroundRoleAndKeepActiveDeclarationsInManualMode(string appId, bool autonomous, ActivationMode expected)
    {
        var factory = new RegistrationFactory(appId, autonomousWork: autonomous);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var policy = app.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Equal(expected, policy.GetActivation(factory.DomainOwner, TypeActivationKind.Outbox));
        Assert.Equal(expected, policy.GetActivation(typeof(object), TypeActivationKind.DataChannel, factory.DomainOwner));
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(factory.DomainOwner, TypeActivationKind.Service));
        Assert.All(factory.Trace.Events.Where(entry => entry.Name.EndsWith(":active", StringComparison.Ordinal)),
            entry => Assert.Equal(autonomous, entry.IsAutonomous));
    }

    [Theory]
    [InlineData("api", new[] { "Orders:passive", "orders.api:passive", "orders.worker:passive", "orders.api:active", "module:services" })]
    [InlineData("worker", new[] { "Orders:passive", "orders.api:passive", "orders.worker:passive", "Orders:active", "orders.worker:active", "module:services" })]
    public async Task Compose_ShouldRegisterEveryIncludedPassiveOwnerBeforeSelectedActiveOwners(string appId, string[] expected)
    {
        var factory = new RegistrationFactory(appId);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected, factory.Trace.Events.Select(entry => entry.Name));
        Assert.Equal(new[] { "Orders", "orders.api", "orders.worker" },
            app.Services.GetRequiredService<RegistrationOutcome>().Owners);
        Assert.All(factory.Trace.Events.Where(entry => entry.Name.EndsWith(":active", StringComparison.Ordinal)), entry =>
        {
            Assert.True(entry.IsActive);
            Assert.True(entry.IsAutonomous);
            Assert.Same(factory.Trace.Events.Single(item => item.Name == entry.Name.Replace(":active", ":passive")).Instance,
                entry.Instance);
        });
        Assert.All(factory.Trace.Events.Where(entry => entry.Instance is not null), entry => Assert.Equal("initial", entry.Input));
        Assert.Equal(1, factory.OptionContributions);
        Assert.Equal(3, factory.Trace.DeferredContributions);
        Assert.Equal("Testing", factory.Trace.EnvironmentName);
    }

    [Theory]
    [InlineData("identity", "frozen")]
    [InlineData("discovery", "frozen")]
    [InlineData("configuration", "read-only")]
    [InlineData("topology", "frozen")]
    [InlineData("recursive", "nonrecursive")]
    public async Task Registration_WhenItChangesFrozenCompositionInputs_ShouldFail(string change, string expected)
    {
        var factory = new RegistrationFactory("api");
        factory.Trace.OnPassive = context =>
        {
            switch (change)
            {
                case "identity": context.Monica.ConfigureApplication(options => options.AppId = "other"); break;
                case "discovery": context.Monica.ConfigureTypeDiscovery(); break;
                case "configuration": context.Configuration["Probe:Value"] = "changed"; break;
                case "topology": factory.FinalOptions!.Applications = new Dictionary<string, ApplicationPlacement>(); break;
                case "recursive": context.Composition.RunRegistration(typeof(RecordingRegistration), () => { }); break;
            }
        };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.Trace.Events, entry => entry.Name == "module:services");
    }

    [Fact]
    public async Task Domain_WhenActiveRegistrationHasNoBackgroundRole_ShouldRejectUnownedCallback()
    {
        var factory = new RegistrationFactory("api", backgroundRole: null);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("BackgroundRole", error.ToString());
        Assert.Empty(factory.Trace.Events);
    }

    [Fact]
    public async Task Domain_WhenOnlyPassiveRegistrationExists_ShouldNotRequireBackgroundRole()
    {
        var factory = new RegistrationFactory("api", backgroundRole: null, passiveDomain: true);
        await using var app = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(factory.Trace.Events, entry => entry.Name == "Orders:passive");
        Assert.DoesNotContain(factory.Trace.Events, entry => entry.Name == "Orders:active");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other.worker")]
    public async Task Domain_WhenBackgroundRoleIsUnknownOrFromAnotherDomain_ShouldFailBeforeCallbacks(string backgroundRole)
    {
        var factory = new RegistrationFactory("api", backgroundRole);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("must name a declared role in domain 'Orders'", error.ToString());
        Assert.Empty(factory.Trace.Events);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("abstract")]
    [InlineData("constructor")]
    public async Task Registration_WhenConstructionContractIsInvalid_ShouldFailBeforeAnyCallback(string defect)
    {
        var factory = new RegistrationFactory("api", registrationDefect: defect);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("public parameterless constructor", error.ToString());
        Assert.Empty(factory.Trace.Events);
    }

    private sealed class RegistrationFactory : MonicaTestApplicationFactory<ApiQuery>
    {
        private readonly string _appId;
        private readonly Assembly[] _assemblies;
        private readonly Type _manifest;
        private readonly bool _autonomousWork;
        internal Type DomainOwner => _assemblies.Single(assembly => assembly.GetCustomAttribute<ApplicationDomainAttribute>() is not null)
            .GetTypes().Single(type => typeof(ApplicationRegistration).IsAssignableFrom(type));
        internal RegistrationTrace Trace { get; } = new();
        internal int OptionContributions { get; private set; }
        internal ModuleApplicationModelOption? FinalOptions { get; private set; }

        internal RegistrationFactory(string appId, string? backgroundRole = "orders.worker", bool passiveDomain = false,
            string? registrationDefect = null, bool autonomousWork = true)
        {
            _appId = appId;
            _autonomousWork = autonomousWork;
            var domain = CreateOwner("Orders", null, backgroundRole,
                passiveDomain ? typeof(PassiveRegistration) : typeof(RecordingRegistration), registrationDefect);
            var api = CreateOwner("Orders", "orders.api", null, typeof(RecordingRegistration));
            var worker = CreateOwner("Orders", "orders.worker", null, typeof(RecordingRegistration));
            var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Test.ApplicationHost.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var manifest = host.DefineDynamicModule("Main").DefineType("HostManifest", TypeAttributes.Public | TypeAttributes.Sealed,
                typeof(FixtureManifest));
            manifest.DefineDefaultConstructor(MethodAttributes.Public);
            manifest.DefineField("Source", typeof(IApplicationManifest), FieldAttributes.Public | FieldAttributes.Static);
            _manifest = manifest.CreateType()!;
            _assemblies = [host, worker, domain, api];
            _manifest.GetField("Source")!.SetValue(null, new FixtureInventory(
                [domain.GetName().Name!, api.GetName().Name!, worker.GetName().Name!],
                [new("Orders", domain.GetName().Name!, backgroundRole), new("Other", "Remote.Other.Domain")],
                [new("orders.api", "Orders", api.GetName().Name!), new("orders.worker", "Orders", worker.GetName().Name!),
                    new("other.worker", "Other", "Remote.Other.Role")]));
        }

        protected override IEnumerable<Assembly> TypeDiscoveryAssemblies => _assemblies;

        public new async Task<MonicaTestApplication> CreateAsync(
            Action<ISeamReplacementBuilder>? configureScenario = null, CancellationToken cancellationToken = default)
        {
            // Attribute Type arguments use assembly-qualified names. Reflection.Emit assemblies are not
            // discoverable by the normal loader, so resolve only this isolated fixture's assembly names.
            Assembly? ResolveFixture(object? sender, ResolveEventArgs args) =>
                _assemblies.FirstOrDefault(assembly => assembly.FullName == args.Name);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFixture;
            try { return await base.CreateAsync(configureScenario, cancellationToken); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= ResolveFixture; }
        }

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Services.AddSingleton(Trace);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Probe:Value"] = "initial" });
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureApplication(options => { options.AppId = _appId; options.AutonomousWork = _autonomousWork; });
            builder.AddApplicationModel(options =>
            {
                OptionContributions++;
                FinalOptions = options;
                options.ManifestType = _manifest;
                options.Applications = new Dictionary<string, ApplicationPlacement>
                {
                    ["api"] = new() { Roles = ["orders.api"] },
                    ["worker"] = new() { Roles = ["orders.worker"] },
                    ["remote"] = new() { Roles = ["other.worker"] }
                };
            });
            builder.Composition.LiveConfiguration["Probe:Value"] = "later";
        }

        private static Assembly CreateOwner(string domain, string? role, string? backgroundRole, Type parent,
            string? defect = null)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Test.ApplicationOwner.{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var registration = assembly.DefineDynamicModule("Main").DefineType("OwnerRegistration",
                TypeAttributes.Public | (defect == "abstract" ? TypeAttributes.Abstract : TypeAttributes.Sealed), parent);
            registration.DefineDefaultConstructor(defect == "constructor" ? MethodAttributes.Private : MethodAttributes.Public);
            registration.DefineField("Owner", typeof(string), FieldAttributes.Public | FieldAttributes.Static);
            var type = registration.CreateType()!;
            type.GetField("Owner")!.SetValue(null, role ?? domain);
            var attribute = role is null ? typeof(ApplicationDomainAttribute) : typeof(ApplicationRoleAttribute);
            var properties = new List<PropertyInfo> { attribute.GetProperty("Registration")! };
            var values = new List<object> { defect == "foreign" ? typeof(RecordingRegistration) : type };
            if (backgroundRole is not null)
            {
                properties.Add(attribute.GetProperty("BackgroundRole")!);
                values.Add(backgroundRole);
            }
            assembly.SetCustomAttribute(new CustomAttributeBuilder(role is null
                    ? attribute.GetConstructor([typeof(object)])! : attribute.GetConstructor([typeof(string), typeof(object)])!,
                role is null ? [domain] : [role, domain], properties.ToArray(), values.ToArray()));
            return type.Assembly;
        }
    }

    public class PassiveRegistration : ApplicationRegistration
    {
        public override void ConfigurePassive(ApplicationRegistrationContext context)
        {
            var trace = (RegistrationTrace)context.Services.Single(descriptor => descriptor.ServiceType == typeof(RegistrationTrace)).ImplementationInstance!;
            var owner = (string)GetType().GetField("Owner")!.GetValue(null)!;
            trace.Record(owner + ":passive", this, context);
            context.Monica.AddModule<RecordingModule, RecordingOptions>(options =>
            {
                trace.DeferredContributions++;
                options.Trace = trace;
                options.Owners.Add(owner);
            });
            trace.OnPassive?.Invoke(context);
        }
    }

    public class RecordingRegistration : PassiveRegistration
    {
        public override void ConfigureActive(ApplicationRegistrationContext context)
        {
            var trace = (RegistrationTrace)context.Services.Single(descriptor => descriptor.ServiceType == typeof(RegistrationTrace)).ImplementationInstance!;
            trace.Record((string)GetType().GetField("Owner")!.GetValue(null)! + ":active", this, context);
        }
    }

    public sealed class RegistrationTrace
    {
        internal List<RegistrationEvent> Events { get; } = [];
        internal Action<ApplicationRegistrationContext>? OnPassive { get; set; }
        internal int DeferredContributions { get; set; }
        internal string? EnvironmentName { get; private set; }
        internal void Record(string name, object instance, ApplicationRegistrationContext context)
        {
            EnvironmentName = context.Environment.EnvironmentName;
            Events.Add(new RegistrationEvent(name, instance, context.IsActive, context.IsAutonomous, context.Configuration["Probe:Value"]));
        }
    }

    internal sealed record RegistrationEvent(string Name, object? Instance = null, bool IsActive = false,
        bool IsAutonomous = false, string? Input = null);
    public sealed record RegistrationOutcome(IReadOnlyList<string> Owners);
    public sealed class RecordingOptions : ModuleOptions<RecordingModule>
    {
        public RegistrationTrace Trace { get; set; } = null!;
        public List<string> Owners { get; } = [];
    }
    public sealed class RecordingModule : MonicaModule<RecordingOptions>
    {
        public override void ConfigureServices(ModuleContext<RecordingOptions> context)
        {
            Option.Trace.Events.Add(new RegistrationEvent("module:services"));
            context.Services.AddSingleton(new RegistrationOutcome(Option.Owners.ToArray()));
        }
    }

    public class FixtureManifest : IApplicationManifest
    {
        private readonly IApplicationManifest _source;
        public FixtureManifest() => _source = (IApplicationManifest)GetType().GetField("Source")!.GetValue(null)!;
        public IReadOnlyList<string> LocalAssemblies => _source.LocalAssemblies;
        public IReadOnlyList<ApplicationDomainDefinition> Domains => _source.Domains;
        public IReadOnlyList<ApplicationRoleDefinition> Roles => _source.Roles;
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }

    private sealed class FixtureInventory(IReadOnlyList<string> localAssemblies,
        IReadOnlyList<ApplicationDomainDefinition> domains, IReadOnlyList<ApplicationRoleDefinition> roles) : IApplicationManifest
    {
        public IReadOnlyList<string> LocalAssemblies => localAssemblies;
        public IReadOnlyList<ApplicationDomainDefinition> Domains => domains;
        public IReadOnlyList<ApplicationRoleDefinition> Roles => roles;
        public IReadOnlyList<ApplicationOperationDefinition> Operations => [];
        public IReadOnlyList<ApplicationApiClientDefinition> ApiClients => [];
    }
}
