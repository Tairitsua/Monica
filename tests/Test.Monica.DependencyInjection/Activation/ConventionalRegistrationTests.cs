using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.DependencyInjection.Annotations;
using Monica.DependencyInjection.Facades;
using Monica.Modules;
using Xunit;

namespace Test.Monica.DependencyInjection.Activation;

public sealed class ConventionalRegistrationTests
{
    private const string KEYED_ONLY_SERVICE_KEY = "keyed-only";

    [Fact]
    public void Composition_WhenLifetimeAndExposureAreInherited_ShouldRegisterDerivedImplementation()
    {
        using var host = BuildHost(enableDiagnostics: false);
        using var firstScope = host.Services.CreateScope();
        using var secondScope = host.Services.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IInheritedService>();
        var repeated = firstScope.ServiceProvider.GetRequiredService<IInheritedService>();
        var second = secondScope.ServiceProvider.GetRequiredService<IInheritedService>();

        first.Should().BeOfType<InheritedService>();
        first.Should().BeSameAs(repeated);
        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void Composition_WhenDefaultExposureIsUsed_ShouldReuseConventionInterfaceAndSelfInstance()
    {
        using var host = BuildHost(enableDiagnostics: false);

        var exposed = host.Services.GetRequiredService<IConventionNamedService>();
        var implementation = host.Services.GetRequiredService<ConventionNamedService>();

        exposed.Should().BeSameAs(implementation);
    }

    [Fact]
    public void Composition_WhenOnlyKeyedExposureIsDeclared_ShouldSuppressDefaultServices()
    {
        using var host = BuildHost(enableDiagnostics: false);

        var keyed = host.Services.GetRequiredKeyedService<IKeyedOnlyService>(KEYED_ONLY_SERVICE_KEY);

        keyed.Should().BeOfType<KeyedOnlyService>();
        host.Services.GetService<IKeyedOnlyService>().Should().BeNull();
        host.Services.GetService<KeyedOnlyService>().Should().BeNull();
    }

    [Fact]
    public async Task Diagnostics_WhenEnabled_ShouldCaptureRegistrationsCreatedDuringDiscoveryCommit()
    {
        using var host = BuildHost(enableDiagnostics: true);
        var facade = host.Services.GetRequiredService<DependencyInjectionDiagnosticsFacade>();

        var result = await facade.GetSnapshotAsync();

        result.Status.Should().Be(ResultStatus.Ok);
        result.Data.Should().NotBeNull();
        result.Data!.Descriptors.Should().Contain(descriptor =>
            descriptor.ServiceType.EndsWith($".{nameof(IConventionNamedService)}", StringComparison.Ordinal)
            && descriptor.Lifetime == ServiceLifetime.Singleton
            && descriptor.IsAutoRegistered);
    }

    private static IHost BuildHost(bool enableDiagnostics)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(ConventionalRegistrationTests).Assembly));
            var dependencyInjection = monica.AddDependencyInjection();
            if (enableDiagnostics)
            {
                dependencyInjection.EnableAutoRegistrationDiagnostics();
            }
        });
        return builder.Build();
    }

    public interface IInheritedService;

    [Dependency(ServiceLifetime.Scoped)]
    [ExposeServices(typeof(IInheritedService))]
    public abstract class InheritedServiceBase : IInheritedService;

    public sealed class InheritedService : InheritedServiceBase;

    public interface IConventionNamedService;

    public sealed class ConventionNamedService : IConventionNamedService, ISingletonDependency;

    public interface IKeyedOnlyService;

    [Dependency(ServiceLifetime.Singleton)]
    [ExposeKeyedService<IKeyedOnlyService>(KEYED_ONLY_SERVICE_KEY)]
    public sealed class KeyedOnlyService : IKeyedOnlyService;
}
