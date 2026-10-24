using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Diagnostics.Facades;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Core.TypeDiscovery.Annotations;
using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ApplicationActivationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task StandalonePolicy_ShouldFreezeInputAndHoldOnlyOrdinaryBackgroundWork(string? input, bool automatic)
    {
        var builder = Host.CreateApplicationBuilder();
        if (input is not null) builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:AutonomousWork"] = input });
        var ordinary = new OrdinaryWork();
        var local = new LocalInitialization();
        var coordinator = new Coordinator();
        builder.Services.AddSingleton<IHostedService>(ordinary);
        builder.Services.AddSingleton<IHostedService>(local);
        var descriptor = ServiceDescriptor.Singleton<IHostedService>(coordinator);
        builder.Services.Add(descriptor);
        var factoryCalls = 0;
        builder.Services.AddSingleton<IHostedService>(_ => { factoryCalls++; return new OrdinaryWork(); });
        MonicaApplicationOptions? retained = null;
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.ConfigureApplication(options => retained = options);
            monica.AddModuleSystem();
            monica.Composition.AdmitHostedLifecycle(descriptor, typeof(Coordinator));
        });
        Assert.Equal(0, factoryCalls);
        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();
        var policy = host.Services.GetRequiredService<ITypeActivationPolicy>();
        Assert.Same(application.TypeActivation, policy);
        Assert.Equal(automatic, application.Application.AutonomousWork);
        Assert.Throws<InvalidOperationException>(() => retained!.AutonomousWork = !automatic);
        builder.Configuration["Application:AutonomousWork"] = (!automatic).ToString();
        Assert.Equal(automatic, application.Application.AutonomousWork);
        Assert.Equal(ActivationMode.Automatic, policy.GetActivation(typeof(OrdinaryWork), TypeActivationKind.Service));
        Assert.Equal(automatic ? ActivationMode.Automatic : ActivationMode.Manual,
            policy.GetActivation(typeof(LocalInitialization), TypeActivationKind.Job));
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(automatic ? 1 : 0, ordinary.Starts);
        Assert.Equal(1, local.Starts);
        Assert.Equal(1, coordinator.Starts);
        Assert.Equal(automatic ? 1 : 0, factoryCalls);
        var opaque = Assert.Single(application.ActivationResources, resource => resource.ImplementationType is null);
        Assert.Equal(automatic ? ActivationMode.Automatic : ActivationMode.Manual, opaque.Mode);
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var startup = facade.GetSnapshot().Data;
        var portable = facade.GetResourceActivations().Data!;
        Assert.Single(portable, resource => resource.ImplementationTypeName is null);
        Assert.Contains(nameof(LocalInitialization), System.Text.Json.JsonSerializer.Serialize(portable));
        application.RecordActivation(new ResourceActivation
        {
            Key = "seeder:prerequisite", ImplementationType = typeof(LocalInitialization),
            Kind = TypeActivationKind.Seeder, Mode = ActivationMode.Manual, Reason = "An eligible prerequisite remains held."
        });
        Assert.DoesNotContain(portable, resource => resource.Key == "seeder:prerequisite");
        Assert.Contains(facade.GetResourceActivations().Data!, resource => resource.Key == "seeder:prerequisite");
        Assert.Same(startup, facade.GetSnapshot().Data);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("invalid", false)]
    [InlineData("false", true)]
    public void StartupPolicy_ShouldRejectInvalidOrConflictingInput(string value, bool configureTrue)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Application:AutonomousWork"] = value });
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            if (configureTrue) monica.ConfigureApplication(options => options.AutonomousWork = true);
        }));
        Assert.Contains("Application:AutonomousWork", error.Message);
    }

    [Theory]
    [InlineData(false, "remove")]
    [InlineData(false, "replace")]
    [InlineData(false, "append")]
    [InlineData(true, "remove")]
    [InlineData(true, "replace")]
    [InlineData(true, "append")]
    public void Composition_WhenLateRegistrationChangesFrozenPolicy_ShouldRejectBeforeResolvingIt(
        bool duringFinalization, string mutation)
    {
        var builder = Host.CreateApplicationBuilder();
        var factoryCalls = 0;
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.ConfigureApplication(options => options.AutonomousWork = false);
            monica.AddModuleSystem().PostConfigureServices(context =>
            {
                void ChangePolicy(IServiceCollection services)
                {
                    if (mutation is "remove" or "replace") services.RemoveAll<ITypeActivationPolicy>();
                    if (mutation is "replace" or "append")
                        services.AddSingleton<ITypeActivationPolicy>(_ =>
                        {
                            factoryCalls++;
                            return new PermissivePolicy();
                        });
                }

                if (duringFinalization) context.Composition.OnServicesFinalized(ChangePolicy);
                else ChangePolicy(context.Services);
            });
        }));

        Assert.Contains("frozen ITypeActivationPolicy singleton", error.Message);
        Assert.Equal(0, factoryCalls);
    }

    private sealed class PermissivePolicy : ITypeActivationPolicy
    {
        public ActivationMode GetActivation(Type implementationType, TypeActivationKind kind, Type? registrationOwner = null)
            => ActivationMode.Automatic;
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType, Type? registrationOwner = null) => null;
    }

    private class OrdinaryWork : IHostedService
    {
        internal int Starts { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    [ProcessLocal]
    private sealed class LocalInitialization : OrdinaryWork;
    private sealed class Coordinator : OrdinaryWork;
}
