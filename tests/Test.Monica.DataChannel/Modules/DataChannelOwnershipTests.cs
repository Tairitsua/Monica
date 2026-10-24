using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Providers.Default;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.DataChannel.Modules;

public sealed class DataChannelOwnershipTests
{
    [Fact]
    public async Task SharedSetup_ShouldRetainRoleOwnerThroughMaterialization()
    {
        var probe = new Probe();
        await using var host = await new OwnershipFactory(probe, conflictingOwner: false)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        var channel = Assert.Single(host.Services.GetRequiredService<IDataChannelManager>().FetchAll());
        Assert.True(channel.Pipe.IsInitialized);
        Assert.Equal(1, probe.SetupCalls);
        var resources = host.Services.GetRequiredService<MonicaApplication>().ActivationResources;
        Assert.Contains(resources, resource => resource.Key == "DataChannel:channel:shared"
            && resource.ImplementationType == typeof(SharedSetup)
            && resource.RegistrationOwner == typeof(SelectedRole)
            && resource.Mode == ActivationMode.Automatic);
    }

    [Fact]
    public async Task RepeatedSetup_WithDifferentOwners_ShouldFailBeforeConstruction()
    {
        var probe = new Probe();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OwnershipFactory(probe, conflictingOwner: true)
                .CreateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("conflicting registration owners", error.Message);
        Assert.Equal(0, probe.SetupCalls);
        Assert.Equal(0, probe.Constructions);
    }

    private sealed class OwnershipFactory(Probe probe, bool conflictingOwner)
        : MonicaTestApplicationFactory<DataChannelOwnershipTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddModule<OwnershipAuthority, OwnershipOptions>(options => options.ConflictingOwner = conflictingOwner);
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(probe);
        }
    }

    public sealed class OwnershipAuthority : MonicaModule<OwnershipOptions>, IApplicationCompositionModule
    {
        public void PrepareApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder) =>
            Application.SetTypeActivationPolicy(new OwnershipPolicy());

        public void RegisterApplication(IMonicaBuilder builder, IHostApplicationBuilder hostBuilder)
        {
            builder.Composition.RunRegistration(typeof(SelectedRole), () =>
                builder.AddDataChannel().UseSetup<SharedSetup>());
            builder.Composition.RunRegistration(Option.ConflictingOwner ? typeof(OtherRole) : typeof(SelectedRole), () =>
                builder.AddDataChannel().UseSetup<SharedSetup>());
        }
    }

    public sealed class OwnershipOptions : ModuleOptions<OwnershipAuthority>
    {
        public bool ConflictingOwner { get; set; }
    }

    private sealed class OwnershipPolicy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null) =>
            kind != TypeActivationKind.DataChannel || registrationOwner == typeof(SelectedRole)
                ? ActivationMode.Automatic : ActivationMode.Disabled;

        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }

    private sealed class Probe
    {
        internal int Constructions;
        internal int SetupCalls;
    }

    private sealed class SharedSetup : IDataChannelSetup
    {
        private readonly Probe _probe;

        public SharedSetup(Probe probe)
        {
            _probe = probe;
            probe.Constructions++;
        }

        public void Setup(IDataChannelRegistrar channels)
        {
            _probe.SetupCalls++;
            channels.Add("shared", pipeline => pipeline.SetOuterEndpoint(new DefaultEndpointOptions()));
        }
    }

    private sealed class SelectedRole;
    private sealed class OtherRole;
}
