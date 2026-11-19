using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ModuleCompilerInvariantTests
{
    [Fact]
    public void AddMonica_WhenHardDependencyCycleHasAcyclicDependent_ShouldReportOnlyCyclePath()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<HardCycleDependentModule, HardCycleDependentModuleOption>());

        var exception = compose.Should().Throw<ModuleRegistrationException>().Which;

        exception.Message.Should()
            .Contain(nameof(HardCycleLeftModule))
            .And.Contain(nameof(HardCycleRightModule))
            .And.NotContain(nameof(HardCycleDependentModule));
        exception.Message.Split(Environment.NewLine)
            .Should().ContainSingle(static line => line.StartsWith("- ", StringComparison.Ordinal));
    }

    [Fact]
    public void AddMonica_WhenOptionalOrderingCycleHasAcyclicDependent_ShouldReportOnlyCyclePath()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
        {
            monica.AddModule<OptionalCycleDependentModule, OptionalCycleDependentModuleOption>();
            monica.AddModule<OptionalCycleLeftModule, OptionalCycleLeftModuleOption>();
            monica.AddModule<OptionalCycleRightModule, OptionalCycleRightModuleOption>();
        });

        var exception = compose.Should().Throw<ModuleRegistrationException>().Which;

        exception.Message.Should()
            .Contain(nameof(OptionalCycleLeftModule))
            .And.Contain(nameof(OptionalCycleRightModule))
            .And.NotContain(nameof(OptionalCycleDependentModule));
        exception.Message.Split(Environment.NewLine)
            .Should().ContainSingle(static line => line.StartsWith("- ", StringComparison.Ordinal));
    }

    [Fact]
    public void AddMonica_WhenModulesHaveDependencies_ShouldCompleteEveryBuilderCallbackBeforeServiceCallbacks()
    {
        var events = new List<string>();
        var builder = Host.CreateApplicationBuilder();

        builder.AddMonica(monica =>
        {
            monica.AddModule<LifecycleDependentModule, LifecycleDependentModuleOption>(options =>
                options.Events = events);
            monica.AddModule<LifecycleDependencyModule, LifecycleDependencyModuleOption>(options =>
                options.Events = events);
        });
        using var host = builder.Build();

        events.Should().Equal(
            "builder:dependency",
            "builder:dependent",
            "services:dependency",
            "services:dependent");
    }

    [Fact]
    public void AddMonica_WhenTypeDiscoveryDeclarationFails_ShouldNotMutateHostBuilderOrServices()
    {
        var builder = Host.CreateApplicationBuilder();
        var originalServiceCount = builder.Services.Count;

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<FailingDiscoveryModule, FailingDiscoveryModuleOption>());

        var exception = compose.Should().Throw<Exception>().Which;

        exception.Message.Should().Contain("Type-discovery plan compilation failed before host mutation");
        exception.InnerException.Should().NotBeNull();
        exception.InnerException!.Message.Should().Be("discovery-declaration-failure");
        ((IHostApplicationBuilder)builder).Properties
            .ContainsKey(FailingDiscoveryModule.BuilderPropertyKey)
            .Should().BeFalse();
        builder.Services.Should().HaveCount(originalServiceCount);
        builder.Services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(DiscoveryMutationMarker));
    }

    [Fact]
    public void AddMonica_WhenServiceConfigurationFails_ShouldMakeTheMutatedBuilderTerminal()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
            monica.AddModule<FailingServiceModule, FailingServiceModuleOption>());

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("service-configuration-failure");

        Action retry = () => builder.AddMonica(static _ => { });
        retry.Should().Throw<InvalidOperationException>()
            .WithMessage("*only once*");
    }

    [Fact]
    public void ModuleRuntimeSnapshot_PublicProperties_ShouldNotExposeSetters()
    {
        var publicSettableProperties = typeof(ModuleRuntimeSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.SetMethod?.IsPublic == true)
            .Select(static property => property.Name)
            .ToArray();

        publicSettableProperties.Should().BeEmpty();
    }

    [Fact]
    public void ModuleRegistrationState_ShouldNotBePublic()
    {
        var registrationState = typeof(ModuleRuntimeSnapshot).Assembly.GetType(
            "Monica.Core.Modularity.Models.Internal.ModuleRegistrationState",
            throwOnError: true)!;

        registrationState.IsPublic.Should().BeFalse();
    }
}

internal sealed class HardCycleDependentModule : MonicaModule<HardCycleDependentModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<HardCycleLeftModule>();
    }
}

internal sealed class HardCycleDependentModuleOption : ModuleOptions<HardCycleDependentModule>;

internal sealed class HardCycleLeftModule : MonicaModule<HardCycleLeftModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<HardCycleRightModule>();
    }
}

internal sealed class HardCycleLeftModuleOption : ModuleOptions<HardCycleLeftModule>;

internal sealed class HardCycleRightModule : MonicaModule<HardCycleRightModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<HardCycleLeftModule>();
    }
}

internal sealed class HardCycleRightModuleOption : ModuleOptions<HardCycleRightModule>;

internal sealed class OptionalCycleDependentModule : MonicaModule<OptionalCycleDependentModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<OptionalCycleLeftModule>();
    }
}

internal sealed class OptionalCycleDependentModuleOption : ModuleOptions<OptionalCycleDependentModule>;

internal sealed class OptionalCycleLeftModule : MonicaModule<OptionalCycleLeftModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<OptionalCycleRightModule>();
    }
}

internal sealed class OptionalCycleLeftModuleOption : ModuleOptions<OptionalCycleLeftModule>;

internal sealed class OptionalCycleRightModule : MonicaModule<OptionalCycleRightModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<OptionalCycleLeftModule>();
    }
}

internal sealed class OptionalCycleRightModuleOption : ModuleOptions<OptionalCycleRightModule>;

internal sealed class LifecycleDependentModule : MonicaModule<LifecycleDependentModuleOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<LifecycleDependencyModule>();
    }

    public override void ConfigureBuilder(ModuleBuilderContext<LifecycleDependentModuleOption> context)
    {
        Option.Events.Add("builder:dependent");
    }

    public override void ConfigureServices(ModuleContext<LifecycleDependentModuleOption> context)
    {
        Option.Events.Add("services:dependent");
    }
}

internal sealed class LifecycleDependentModuleOption : ModuleOptions<LifecycleDependentModule>
{
    internal List<string> Events { get; set; } = [];
}

internal sealed class LifecycleDependencyModule : MonicaModule<LifecycleDependencyModuleOption>
{
    public override void ConfigureBuilder(ModuleBuilderContext<LifecycleDependencyModuleOption> context)
    {
        Option.Events.Add("builder:dependency");
    }

    public override void ConfigureServices(ModuleContext<LifecycleDependencyModuleOption> context)
    {
        Option.Events.Add("services:dependency");
    }
}

internal sealed class LifecycleDependencyModuleOption : ModuleOptions<LifecycleDependencyModule>
{
    internal List<string> Events { get; set; } = [];
}

internal sealed class FailingDiscoveryModule : MonicaModule<FailingDiscoveryModuleOption>
{
    internal static object BuilderPropertyKey { get; } = new();

    public override void ConfigureBuilder(ModuleBuilderContext<FailingDiscoveryModuleOption> context)
    {
        context.HostApplicationBuilder.Properties[BuilderPropertyKey] = true;
    }

    public override void ConfigureServices(ModuleContext<FailingDiscoveryModuleOption> context)
    {
        context.Services.AddSingleton<DiscoveryMutationMarker>();
    }

    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<FailingDiscoveryModuleOption> discovery)
    {
        throw new InvalidOperationException("discovery-declaration-failure");
    }
}

internal sealed class FailingDiscoveryModuleOption : ModuleOptions<FailingDiscoveryModule>;

internal sealed class DiscoveryMutationMarker;

internal sealed class FailingServiceModule : MonicaModule<FailingServiceModuleOption>
{
    public override void ConfigureServices(ModuleContext<FailingServiceModuleOption> context)
    {
        throw new InvalidOperationException("service-configuration-failure");
    }
}

internal sealed class FailingServiceModuleOption : ModuleOptions<FailingServiceModule>;
