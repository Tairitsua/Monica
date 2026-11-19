using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Diagnostics.Services;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ModuleGraphCompilationTests
{
    [Fact]
    public void AddMonica_WhenDependencyAndHostContributeOptions_ShouldApplyHostContributionLast()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddModule<GraphDependencyModule, GraphDependencyModuleOption>(options =>
                options.Contributions.Add("host"));
            monica.AddModule<GraphConsumerModule, GraphConsumerModuleOption>();
        });

        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptions<GraphDependencyModuleOption>>().Value;
        var observation = host.Services.GetRequiredService<GraphOptionObservation>();

        options.Contributions.Should().Equal("dependency", "host");
        observation.DependencyOptions.Should().BeSameAs(options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddMonica_WhenFluentDependenciesAreDeclared_ShouldIncludeRequiredAndOrderPresentModules(
        bool includeOptionalTarget)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.AddModule<IndependentGraphModule, IndependentGraphModuleOption>()
                .Require<GraphConsumerModule>()
                .AfterIfPresent<OptionalTargetModule>();
            if (includeOptionalTarget)
            {
                monica.AddModule<OptionalTargetModule, OptionalTargetModuleOption>();
            }
        });

        using var host = builder.Build();
        var moduleTypes = host.Services.GetRequiredService<MonicaApplication>()
            .Modules.RuntimeSnapshots
            .Select(static snapshot => snapshot.ModuleType)
            .ToArray();

        moduleTypes.Should().ContainInOrder(
            typeof(GraphDependencyModule), typeof(GraphConsumerModule), typeof(IndependentGraphModule));
        if (includeOptionalTarget)
        {
            moduleTypes.Should().ContainInOrder(typeof(OptionalTargetModule), typeof(IndependentGraphModule));
        }
        else
        {
            moduleTypes.Should().NotContain(typeof(OptionalTargetModule));
        }
    }

    [Fact]
    public void AddMonica_WhenTypedDependencySuppliesNoOptionDefaults_ShouldIncludeDependency()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
            monica.AddModule<LegacyDependencyConsumerModule, LegacyDependencyConsumerModuleOption>());

        using var host = builder.Build();
        host.Services.GetRequiredService<GraphDependencyModuleOption>().Contributions.Should().BeEmpty();
    }

    [Fact]
    public void AddMonica_WhenRequiredModuleUsesOptionsOwnedByAnotherModule_ShouldRejectComposition()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<IndependentGraphModule, IndependentGraphModuleOption>()
                .Require<MismatchedOptionsGraphModule>());

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*MismatchedOptionsGraphModule*GraphDependencyModuleOption*ModuleOptions*");
    }

    [Fact]
    public void AddMonica_WhenOptionalOrderingTargetIsAbsent_ShouldNotIncludeIt()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
            monica.AddModule<OptionalFollowerModule, OptionalFollowerModuleOption>());

        using var host = builder.Build();
        var moduleTypes = host.Services.GetRequiredService<MonicaApplication>()
            .Modules.RuntimeSnapshots
            .Select(static snapshot => snapshot.ModuleType)
            .ToArray();

        moduleTypes.Should().Contain(typeof(OptionalFollowerModule));
        moduleTypes.Should().NotContain(typeof(OptionalTargetModule));
        host.Services.GetRequiredService<OptionalOptionObservation>().WasPresent.Should().BeFalse();
    }

    [Fact]
    public void AddMonica_WhenModuleIsExplicitlyDisabled_ShouldOmitItBeforeOptionsAreMaterialized()
    {
        var builder = Host.CreateApplicationBuilder();
        var optionsMaterialized = false;
        builder.AddMonica(monica => monica
            .AddModule<DisabledGraphModule, DisabledGraphModuleOption>(_ => optionsMaterialized = true)
            .Disable("Disabled for this host"));

        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();
        var diagnostics = CreateDiagnostics(host.Services);

        optionsMaterialized.Should().BeFalse();
        application.Modules.IsRegistered(typeof(DisabledGraphModule)).Should().BeFalse();
        application.Modules.RuntimeSnapshots.Should().NotContain(
            snapshot => snapshot.ModuleType == typeof(DisabledGraphModule));
        diagnostics.Modules.Should().ContainSingle(module =>
            module.TypeName == nameof(DisabledGraphModule) && !module.IsActive);
        diagnostics.Modules.Single(module => module.TypeName == nameof(DisabledGraphModule)).DisabledReason
            .Should().Be("Disabled for this host");
    }

    [Fact]
    public void AddMonica_WhenRequiredModuleIsDisabled_ShouldDisableOnlyHardDependents()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.AddModule<DisabledGraphModule, DisabledGraphModuleOption>()
                .Disable("Provider intentionally omitted");
            monica.AddModule<DisabledGraphDependentModule, DisabledGraphDependentModuleOption>();
            monica.AddModule<IndependentGraphModule, IndependentGraphModuleOption>();
        });

        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();
        var runtimeTypes = application.Modules.RuntimeSnapshots
            .Select(static snapshot => snapshot.ModuleType)
            .ToArray();
        var diagnostics = CreateDiagnostics(host.Services);

        runtimeTypes.Should().Equal(typeof(IndependentGraphModule));
        diagnostics.Modules.Where(static module => !module.IsActive).Select(static module => module.TypeName)
            .Should().BeEquivalentTo(
                nameof(DisabledGraphModule),
                nameof(DisabledGraphDependentModule));
        diagnostics.Modules.Single(module => module.TypeName == nameof(DisabledGraphDependentModule)).DisabledReason
            .Should().Contain(nameof(DisabledGraphModule));
    }

    [Fact]
    public void AddMonica_WhenOptionalOrderingTargetIsDisabled_ShouldKeepFollowerActive()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.AddModule<OptionalTargetModule, OptionalTargetModuleOption>()
                .Disable("Optional capability omitted");
            monica.AddModule<OptionalFollowerModule, OptionalFollowerModuleOption>();
        });

        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();

        application.Modules.IsRegistered(typeof(OptionalTargetModule)).Should().BeFalse();
        application.Modules.IsRegistered(typeof(OptionalFollowerModule)).Should().BeTrue();
    }

    [Fact]
    public void AddMonica_WhenRetainedRegistrationIsMutated_ShouldRejectMutation()
    {
        var builder = Host.CreateApplicationBuilder();
        ModuleRegistration<IndependentGraphModule, IndependentGraphModuleOption>? registration = null;
        builder.AddMonica(monica =>
            registration = monica.AddModule<IndependentGraphModule, IndependentGraphModuleOption>());

        Action mutate = () => registration!.Disable("Too late");

        mutate.Should().Throw<InvalidOperationException>()
            .WithMessage("*graph is sealed*");
    }

    private static ModuleDiagnosticsSnapshot CreateDiagnostics(IServiceProvider services)
    {
        return new ModuleDiagnosticsService(
                services.GetRequiredService<MonicaApplication>(),
                Options.Create(new ModuleSystemOption()),
                services.GetRequiredService<IHostEnvironment>())
            .GetSnapshot();
    }
}

internal sealed class GraphConsumerModule : MonicaModule<GraphConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<GraphDependencyModule, GraphDependencyModuleOption>(options =>
            options.Contributions.Add("dependency"));
    }

    public override void ConfigureServices(ModuleContext<GraphConsumerModuleOption> context)
    {
        var dependencyOptions = context.Modules.Get<GraphDependencyModule, GraphDependencyModuleOption>();
        context.Services.AddSingleton(new GraphOptionObservation(dependencyOptions));
    }
}

internal sealed class GraphConsumerModuleOption : ModuleOptions<GraphConsumerModule>;

internal sealed class GraphDependencyModule : MonicaModule<GraphDependencyModuleOption>;

internal sealed class GraphDependencyModuleOption : ModuleOptions<GraphDependencyModule>
{
    internal List<string> Contributions { get; } = [];
}

internal sealed class LegacyDependencyConsumerModule : MonicaModule<LegacyDependencyConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<GraphDependencyModule, GraphDependencyModuleOption>(null);
    }
}

internal sealed class LegacyDependencyConsumerModuleOption : ModuleOptions<LegacyDependencyConsumerModule>;

internal sealed class MismatchedOptionsGraphModule : MonicaModule<GraphDependencyModuleOption>;

internal sealed record GraphOptionObservation(GraphDependencyModuleOption DependencyOptions);

internal sealed class OptionalFollowerModule : MonicaModule<OptionalFollowerModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<OptionalTargetModule>();
    }

    public override void ConfigureServices(ModuleContext<OptionalFollowerModuleOption> context)
    {
        var wasPresent = context.Modules.TryGet<OptionalTargetModule, OptionalTargetModuleOption>(out _);
        context.Services.AddSingleton(new OptionalOptionObservation(wasPresent));
    }
}

internal sealed class OptionalFollowerModuleOption : ModuleOptions<OptionalFollowerModule>;

internal sealed record OptionalOptionObservation(bool WasPresent);

internal sealed class OptionalTargetModule : MonicaModule<OptionalTargetModuleOption>;

internal sealed class OptionalTargetModuleOption : ModuleOptions<OptionalTargetModule>;

internal sealed class DisabledGraphModule : MonicaModule<DisabledGraphModuleOption>;

internal sealed class DisabledGraphModuleOption : ModuleOptions<DisabledGraphModule>;

internal sealed class DisabledGraphDependentModule : MonicaModule<DisabledGraphDependentModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<DisabledGraphModule>();
    }
}

internal sealed class DisabledGraphDependentModuleOption : ModuleOptions<DisabledGraphDependentModule>;

internal sealed class IndependentGraphModule : MonicaModule<IndependentGraphModuleOption>;

internal sealed class IndependentGraphModuleOption : ModuleOptions<IndependentGraphModule>;
