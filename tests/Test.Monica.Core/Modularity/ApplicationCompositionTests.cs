using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ApplicationCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Compose_ShouldPrepareOnceAfterAllInputsAndAllowNewModuleDependencies(bool authorityFirst)
    {
        var hostBuilder = NewBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Application:AppId"] = "local", ["Application:DeploymentScope"] = "test-site"
        });
        var visits = new List<string>();
        MonicaApplicationOptions? retainedIdentity = null;
        TypeFinderOptions? retainedDiscovery = null;
        var optionCalls = 0;
        hostBuilder.AddMonica(builder =>
        {
            builder.ConfigureApplication(options => retainedIdentity = options);
            builder.ConfigureTypeDiscovery(options => { retainedDiscovery = options; options.ExcludeDefault(); });
            void AddAuthority() => builder.AddModule<PreSealAuthority, PreSealOptions>(options =>
            {
                optionCalls++;
                options.Inputs.Add("host");
                options.Prepare = (monica, host, application) =>
                {
                    options.Inputs.Should().Equal("dependency", "host");
                    application.Application.AppId.Should().Be("local");
                    application.ProjectApplicationIdentity("local", "Local application");
                    visits.Add("prepare");
                };
                options.Register = (monica, host, application) =>
                {
                    visits.Add("register");
                    Action identity = () => retainedIdentity!.AppName = "late";
                    identity.Should().Throw<InvalidOperationException>();
                    Action discovery = () => retainedDiscovery!.Add("Late.");
                    discovery.Should().Throw<InvalidOperationException>();
                    monica.Composition.RunRegistration(typeof(FirstOwner), () =>
                        monica.AddModule<AddedDuringRegistration, AddedOptions>());
                };
                options.Services = () => visits.Add("services");
            });
            if (authorityFirst) AddAuthority();
            builder.AddModule<AuthorityDefaults, AuthorityDefaultsOptions>();
            if (!authorityFirst) AddAuthority();
        });
        using var host = hostBuilder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();
        application.Application.AppId.Should().Be("local");
        application.Application.AppName.Should().Be("Local application");
        application.Application.DeploymentScope.Should().Be("test-site");
        optionCalls.Should().Be(1);
        visits.Should().Equal("prepare", "register", "services");
        host.Services.GetRequiredService<AddedMarker>().AppName.Should().Be("Local application");
        application.Modules.RuntimeSnapshots.Count(snapshot => snapshot.ModuleType == typeof(PreSealAuthority)).Should().Be(1);
        host.Services.GetRequiredService<IOptions<PreSealOptions>>().Value.Inputs.Should().Equal("dependency", "host");
        hostBuilder.Configuration["Application:AppId"] = "candidate";
        application.Application.AppId.Should().Be("local");
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("identity")]
    [InlineData("discovery")]
    [InlineData("sources")]
    [InlineData("recursive")]
    [InlineData("second-authority")]
    public void Register_ShouldRejectChangesToPreparedInputsOrRecursiveComposition(string change)
    {
        var hostBuilder = NewBuilder();
        Action compose = () => hostBuilder.AddMonica(builder =>
        {
            builder.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            builder.AddModule<PreSealAuthority, PreSealOptions>(options => options.Register = (monica, host, _) =>
                monica.Composition.RunRegistration(typeof(FirstOwner), () =>
                {
                    switch (change)
                    {
                        case "authority": monica.AddModule<PreSealAuthority, PreSealOptions>(); break;
                        case "identity": monica.ConfigureApplication(identity => identity.AppId = "late"); break;
                        case "discovery": monica.ConfigureTypeDiscovery(); break;
                        case "sources": host.Configuration.AddInMemoryCollection(); break;
                        case "recursive": monica.Composition.RunRegistration(typeof(SecondOwner), () => { }); break;
                        case "second-authority": monica.AddModule<AnotherAuthority, AnotherAuthorityOptions>(); break;
                    }
                }));
        });
        compose.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_ShouldRejectIdentityThatContradictsCanonicalStartupInput(bool fromPreparation)
    {
        var hostBuilder = NewBuilder();
        hostBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:AppId"] = "canonical" });
        Action compose = () => hostBuilder.AddMonica(builder =>
        {
            builder.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            if (!fromPreparation) builder.ConfigureApplication(options => options.AppId = "contradiction");
            builder.AddModule<PreSealAuthority, PreSealOptions>(options => options.Prepare = (monica, _, application) =>
            {
                monica.ConfigureApplication(identity => identity.AppId = "contradiction");
                application.ProjectApplicationIdentity("contradiction", "Display name");
            });
        });
        compose.Should().Throw<InvalidOperationException>().WithMessage("*Application:AppId*");
    }

    [Fact]
    public void Contributions_ShouldRetainIndependentOwnersAcrossDeferredWorkAndOpaqueFactories()
    {
        var hostBuilder = NewBuilder();
        var observations = new OwnershipObservations();
        var factoryCalls = 0;
        MonicaCompositionState? composition = null;
        hostBuilder.AddMonica(builder =>
        {
            composition = builder.Composition;
            builder.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            builder.Composition.GetOrAdd(() => observations);
            builder.AddModule<PreSealAuthority, PreSealOptions>(options =>
            {
                options.Prepare = (_, _, application) => application.SetTypeActivationPolicy(new OwnerActivationPolicy());
                options.Register = (monica, host, _) =>
                {
                    foreach (var owner in new[] { typeof(FirstOwner), typeof(SecondOwner) })
                    {
                        monica.Composition.RunRegistration(owner, () =>
                        {
                            host.Services.AddSingleton(new OwnedMarker("direct", owner));
                            var registration = monica.AddModule<SharedOwnedModule, SharedOwnedOptions>(configured =>
                                observations.Record("option", monica.Composition.RegistrationOwner));
                            registration.ConfigureProfile(owner.Name, configured => configured.Owner = monica.Composition.RegistrationOwner);
                            registration.ConfigureBuilder(context =>
                            {
                                context.RegistrationOwner.Should().Be(owner);
                                context.HostApplicationBuilder.Services.AddSingleton(new OwnedMarker("builder", owner));
                            });
                            registration.ConfigureServices(context =>
                            {
                                context.RegistrationOwner.Should().Be(owner);
                                registration.GetProfile(owner.Name).Owner.Should().Be(owner);
                                observations.Schedule!(owner.Name,
                                    () => observations.Record("worker", monica.Composition.RegistrationOwner),
                                    () => context.Services.AddSingleton(new OwnedMarker("commit", owner)));
                            });
                            registration.PostConfigureServices(context =>
                                context.Services.AddSingleton(new OwnedMarker("post", owner)));
                            monica.Composition.OnServicesFinalized(services =>
                            {
                                observations.Record("finalizer", monica.Composition.RegistrationOwner);
                                services.AddSingleton(new OwnedMarker("finalizer", owner));
                                services.AddSingleton<IHostedService>(_ =>
                                {
                                    factoryCalls++;
                                    return new OpaqueHostedService();
                                });
                            }, descriptors =>
                            {
                                observations.Record("validation", monica.Composition.RegistrationOwner);
                                descriptors.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                    && descriptor.ImplementationFactory is not null).Should().Be(1);
                            });
                        });
                    }
                };
            });
        });
        factoryCalls.Should().Be(0);
        observations.ModuleLifecycleOwner.Should().BeNull();
        foreach (var owner in new[] { typeof(FirstOwner), typeof(SecondOwner) })
        {
            observations.Events.Where(item => item.Owner == owner).Select(item => item.Stage)
                .Should().BeEquivalentTo(new[] { "option", "worker", "finalizer", "validation" });
        }
        var markers = hostBuilder.Services.Where(descriptor => descriptor.ImplementationInstance is OwnedMarker).ToArray();
        markers.Should().HaveCount(10);
        foreach (var descriptor in markers)
            composition!.GetServiceRegistrationOwner(descriptor).Should().Be(((OwnedMarker)descriptor.ImplementationInstance!).Owner);
        var factory = hostBuilder.Services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory is not null).Should().ContainSingle().Subject;
        composition!.GetServiceRegistrationOwner(factory).Should().Be(typeof(FirstOwner));
        composition.RegistrationOwner.Should().BeNull();
        using var host = hostBuilder.Build();
        factoryCalls.Should().Be(0);
    }

    private static HostApplicationBuilder NewBuilder() => new(new HostApplicationBuilderSettings { DisableDefaults = true });
    private sealed class FirstOwner;
    private sealed class SecondOwner;
    internal sealed record AddedMarker(string? AppName);
    private sealed record OwnedMarker(string Stage, Type Owner);

    internal sealed class OwnershipObservations
    {
        internal ConcurrentBag<(string Stage, Type? Owner)> Events { get; } = [];
        internal Action<string, Action, Action>? Schedule { get; set; }
        internal Type? ModuleLifecycleOwner { get; set; }
        internal void Record(string stage, Type? owner) => Events.Add((stage, owner));
    }

    private sealed class OwnerActivationPolicy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) =>
            (registrationOwner ?? implementationType) != typeof(SecondOwner) ? ActivationMode.Automatic : ActivationMode.Disabled;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }

    private sealed class OpaqueHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal sealed class PreSealAuthority : MonicaModule<PreSealOptions>, IApplicationCompositionModule
{
    public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) => Option.Prepare?.Invoke(builder, hostBuilder, Application);
    public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) => Option.Register?.Invoke(builder, hostBuilder, Application);
    public override void ConfigureServices(ModuleContext<PreSealOptions> context) => Option.Services?.Invoke();
}

internal sealed class PreSealOptions : ModuleOptions<PreSealAuthority>
{
    internal List<string> Inputs { get; } = [];
    internal Action<IMonicaBuilder, IHostApplicationBuilder, MonicaApplication>? Prepare { get; set; }
    internal Action<IMonicaBuilder, IHostApplicationBuilder, MonicaApplication>? Register { get; set; }
    internal Action? Services { get; set; }
}

internal sealed class AuthorityDefaults : MonicaModule<AuthorityDefaultsOptions>
{
    public override void Describe(ModuleDescriptor module) => module.Require<PreSealAuthority, PreSealOptions>(options => options.Inputs.Add("dependency"));
}
internal sealed class AuthorityDefaultsOptions : ModuleOptions<AuthorityDefaults>;

internal sealed class AddedDuringRegistration : MonicaModule<AddedOptions>
{
    public override void Describe(ModuleDescriptor module) => module.Require<AddedLeaf, AddedLeafOptions>();
    public override void ConfigureServices(ModuleContext<AddedOptions> context)
    {
        context.Modules.Get<AddedLeaf, AddedLeafOptions>().Should().NotBeNull();
        context.Services.AddSingleton(new ApplicationCompositionTests.AddedMarker(Application.Application.AppName));
    }
}
internal sealed class AddedOptions : ModuleOptions<AddedDuringRegistration>;
internal sealed class AddedLeaf : MonicaModule<AddedLeafOptions>;
internal sealed class AddedLeafOptions : ModuleOptions<AddedLeaf>;

internal sealed class AnotherAuthority : MonicaModule<AnotherAuthorityOptions>, IApplicationCompositionModule
{
    public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) { }
    public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) { }
}
internal sealed class AnotherAuthorityOptions : ModuleOptions<AnotherAuthority>;

internal sealed class SharedOwnedModule : MonicaModule<SharedOwnedOptions>
{
    public override void ConfigureServices(ModuleContext<SharedOwnedOptions> context)
    {
        var observations = context.Composition.GetOrAdd(() => new ApplicationCompositionTests.OwnershipObservations());
        observations.ModuleLifecycleOwner = context.Composition.RegistrationOwner;
        observations.Schedule = (name, work, commit) => ScheduleStartupWork(name, work, commit,
            ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion);
    }
}
internal sealed class SharedOwnedOptions : ModuleOptions<SharedOwnedModule>
{
    internal Type? Owner { get; set; }
}
