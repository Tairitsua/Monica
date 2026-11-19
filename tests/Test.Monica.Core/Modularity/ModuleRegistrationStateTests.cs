using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Facades;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ModuleRegistrationStateTests
{
    [Fact]
    public void AddMonica_WhenRequiredFeatureIsMissing_ShouldRejectComposition()
    {
        var builder = Host.CreateApplicationBuilder();
        var optionsMaterialized = false;

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<RequiredFeatureProbeModule, RequiredFeatureProbeModuleOption>(_ =>
                optionsMaterialized = true));

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*RequiredFeatureProbeModule*Provider*");
        optionsMaterialized.Should().BeFalse(
            "feature requirements must be validated before option contributions execute");
    }

    [Fact]
    public void AddMonica_WhenRequiredFeatureIsSatisfied_ShouldCompleteComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddMonica(monica => monica
            .AddModule<RequiredFeatureProbeModule, RequiredFeatureProbeModuleOption>()
            .SatisfyFeature(RequiredFeatureProbeModule.PROVIDER_FEATURE));

        using var host = builder.Build();
        host.Should().NotBeNull();
    }

    [Fact]
    public void AddMonica_WhenSatisfiedFeatureWasNeverRequired_ShouldRejectComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica => monica
            .AddModule<FeaturelessProbeModule, FeaturelessProbeModuleOption>()
            .SatisfyFeature("TypoProvider"));

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*FeaturelessProbeModule*satisfied undeclared features*TypoProvider*");
    }

    [Fact]
    public void AddMonica_WhenConsumerRequiresDependencyFeature_ShouldValidateDependency()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddMonica(monica => monica
            .AddModule<DependencyFeatureConsumerModule, DependencyFeatureConsumerModuleOption>()
            .Require<DependencyFeatureProviderModule, DependencyFeatureProviderModuleOption>()
            .SatisfyFeature(DependencyFeatureProviderModule.PROVIDER_FEATURE));

        using var host = builder.Build();
        host.Should().NotBeNull();
    }

    [Fact]
    public void AddMonica_WhenConsumerRequiresMissingDependencyFeature_ShouldNameDependency()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<DependencyFeatureConsumerModule, DependencyFeatureConsumerModuleOption>());

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*DependencyFeatureProviderModule*provider*");
    }

    [Fact]
    public void AddMonica_WhenDependencyFeatureIsDeclaredBeforeDependency_ShouldRejectDescriptor()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<InvalidDependencyFeatureConsumerModule, InvalidDependencyFeatureConsumerModuleOption>());

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("*InvalidDependencyFeatureConsumerModule*without first declaring Require<DependencyFeatureProviderModule>()*");
    }

    [Fact]
    public void AddMonica_WhenFinalizedContractRequiresContributedKeyedService_ShouldCompleteComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddMonica(monica => monica
            .AddModule<ServiceContractProbeModule, ServiceContractProbeModuleOption>(options =>
                options.ServiceKey = "blue")
            .ConfigureServices(context =>
                context.Services.AddKeyedSingleton<IServiceContractProbe, ServiceContractProbe>("blue")));

        using var host = builder.Build();
        host.Services.GetRequiredKeyedService<IServiceContractProbe>("blue")
            .Should().BeOfType<ServiceContractProbe>();
    }

    [Fact]
    public void AddMonica_WhenFinalizedContractRequiresMissingKeyedService_ShouldRejectComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica => monica
            .AddModule<ServiceContractProbeModule, ServiceContractProbeModuleOption>(options =>
                options.ServiceKey = "missing"));

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*ServiceContractProbeModule*keyed IServiceContractProbe*missing*");
    }

    [Fact]
    public void AddMonica_WhenFinalizedContractRequiresHostOwnedUnkeyedService_ShouldCompleteComposition()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IServiceContractProbe, ServiceContractProbe>();

        builder.AddMonica(monica => monica
            .AddModule<ServiceContractProbeModule, ServiceContractProbeModuleOption>());

        using var host = builder.Build();
        host.Services.GetRequiredService<IServiceContractProbe>().Should().BeOfType<ServiceContractProbe>();
    }

    [Fact]
    public void AddMonica_WhenContractPhaseCompletes_ShouldExposeItInSnapshotAndPortableExport()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModuleSystem();
            monica.AddModule<ServiceContractProbeModule, ServiceContractProbeModuleOption>()
                .ConfigureServices(context =>
                    context.Services.AddSingleton<IServiceContractProbe, ServiceContractProbe>());
        });

        using var host = builder.Build();
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var moduleKey = ModuleKey.FromModuleType(typeof(ServiceContractProbeModule));
        var snapshot = facade.GetSnapshot().Data!;
        var export = facade.CreateExport().Data!;

        snapshot.SchemaVersion.Should().Be(ModuleDiagnosticsSnapshot.CURRENT_SCHEMA_VERSION);
        snapshot.TraceSpans.Should().ContainSingle(span =>
            span.ModuleKey == moduleKey
            && span.ModulePhase == ModulePhase.DeclareContracts
            && span.CallbackKind == ModuleCallbackKind.Lifecycle
            && span.IsComplete);
        export.SchemaVersion.Should().Be(ModuleDiagnosticsSnapshot.CURRENT_SCHEMA_VERSION);
        export.TraceSpans.Should().ContainSingle(span =>
            span.ModuleId == moduleKey.Id
            && span.ModulePhase == ModulePhase.DeclareContracts
            && span.CallbackKind == ModuleCallbackKind.Lifecycle);
    }
}

internal sealed class RequiredFeatureProbeModule : MonicaModule<RequiredFeatureProbeModuleOption>
{
    public const string PROVIDER_FEATURE = "Provider";

    public override void Describe(ModuleDescriptor module)
    {
        module.RequireFeature(PROVIDER_FEATURE);
    }
}

internal sealed class RequiredFeatureProbeModuleOption : ModuleOptions<RequiredFeatureProbeModule>;

internal sealed class FeaturelessProbeModule : MonicaModule<FeaturelessProbeModuleOption>;

internal sealed class FeaturelessProbeModuleOption : ModuleOptions<FeaturelessProbeModule>;

internal sealed class DependencyFeatureConsumerModule : MonicaModule<DependencyFeatureConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DependencyFeatureProviderModule>();
        module.RequireDependencyFeature<DependencyFeatureProviderModule>(
            DependencyFeatureProviderModule.PROVIDER_FEATURE);
    }
}

internal sealed class DependencyFeatureConsumerModuleOption : ModuleOptions<DependencyFeatureConsumerModule>;

internal sealed class DependencyFeatureProviderModule : MonicaModule<DependencyFeatureProviderModuleOption>
{
    public const string PROVIDER_FEATURE = "provider";
}

internal sealed class DependencyFeatureProviderModuleOption : ModuleOptions<DependencyFeatureProviderModule>;

internal sealed class InvalidDependencyFeatureConsumerModule
    : MonicaModule<InvalidDependencyFeatureConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.RequireDependencyFeature<DependencyFeatureProviderModule>(
            DependencyFeatureProviderModule.PROVIDER_FEATURE);
    }
}

internal sealed class InvalidDependencyFeatureConsumerModuleOption
    : ModuleOptions<InvalidDependencyFeatureConsumerModule>;

internal sealed class ServiceContractProbeModule : MonicaModule<ServiceContractProbeModuleOption>
{
    public override void DeclareContracts(ModuleContractDescriptor<ServiceContractProbeModuleOption> contracts)
    {
        if (contracts.Options.ServiceKey is { } serviceKey)
        {
            contracts.RequireKeyedService<IServiceContractProbe>(serviceKey);
        }
        else
        {
            contracts.RequireService<IServiceContractProbe>();
        }
    }
}

internal sealed class ServiceContractProbeModuleOption : ModuleOptions<ServiceContractProbeModule>
{
    public string? ServiceKey { get; set; }
}

internal interface IServiceContractProbe;

internal sealed class ServiceContractProbe : IServiceContractProbe;
