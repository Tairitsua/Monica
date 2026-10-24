using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class MonicaCompositionStateTests
{
    [Fact]
    public void FinalizeServices_ShouldObserveAllModuleCallbacksAndBarrierCommits()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Settings:Value"] = "captured" });
        MonicaCompositionState? captured = null;
        var finalizerCount = 0;
        builder.AddMonica(monica =>
        {
            captured = monica.Composition;
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            monica.AddModule<FinalizerProbeModule, FinalizerProbeOptions>();
            var state = monica.Composition.GetOrAdd(() => new ProbeState());
            state.Value = "host-owned";
            monica.Composition.OnServicesFinalized(services =>
            {
                finalizerCount++;
                services.Should().Contain(descriptor => descriptor.ServiceType == typeof(PostMarker));
                services.Should().Contain(descriptor => descriptor.ServiceType == typeof(BarrierMarker));
                monica.Composition.GetOrAdd<ProbeState>(() => throw new InvalidOperationException()).Should().BeSameAs(state);
                Action append = () => monica.Composition.OnServicesFinalized(_ => { });
                append.Should().Throw<InvalidOperationException>();
                monica.Composition.ValidateEnvironmentFiles(_ => { });
            });
            builder.Configuration["Settings:Value"] = "live";
        });
        using var host = builder.Build();
        var composition = host.Services.GetRequiredService<MonicaApplication>().Composition;
        composition.Should().BeSameAs(captured);
        composition.StartupConfiguration["Settings:Value"].Should().Be("captured");
        composition.LiveConfiguration["Settings:Value"].Should().Be("live");
        composition.GetOrAdd(() => new ProbeState()).Value.Should().Be("host-owned");
        finalizerCount.Should().Be(1);
        Action lateState = () => composition.GetOrAdd(() => new UnregisteredState());
        lateState.Should().Throw<InvalidOperationException>();
        Action lateValidation = () => composition.ValidateEnvironmentFiles(_ => { });
        lateValidation.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FinalizeServices_ShouldCompleteEveryRegistrationBeforeCheckingTheSameReadOnlySnapshot()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var calls = new List<string>();
        IReadOnlyList<ServiceDescriptor>? firstSnapshot = null;
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            monica.Composition.OnServicesFinalized(services =>
            {
                calls.Add("complete-first");
                services.AddSingleton<PostMarker>();
            }, descriptors =>
            {
                calls.Add("validate-first");
                descriptors.Should().Contain(descriptor => descriptor.ServiceType == typeof(BarrierMarker));
                firstSnapshot = descriptors;
                Action changeSnapshot = () => ((IList<ServiceDescriptor>)descriptors).Clear();
                changeSnapshot.Should().Throw<NotSupportedException>();
                Action append = () => monica.Composition.OnServicesFinalized(_ => { });
                append.Should().Throw<InvalidOperationException>();
            });
            monica.Composition.OnServicesFinalized(services =>
            {
                calls.Add("complete-second");
                services.AddSingleton<BarrierMarker>();
            }, descriptors =>
            {
                calls.Add("validate-second");
                descriptors.Should().BeSameAs(firstSnapshot);
            });
        });
        using var host = builder.Build();
        calls.Should().Equal("complete-first", "complete-second", "validate-first", "validate-second");
    }

    [Fact]
    public void GetOrAdd_WhenTwoHostsUseTheSameStateType_ShouldKeepTheirInstancesSeparate()
    {
        using var first = CreateHost("first");
        using var second = CreateHost("second");
        var firstState = first.Services.GetRequiredService<MonicaApplication>().Composition.GetOrAdd(() => new ProbeState());
        var secondState = second.Services.GetRequiredService<MonicaApplication>().Composition.GetOrAdd(() => new ProbeState());
        firstState.Should().NotBeSameAs(secondState);
        firstState.Value.Should().Be("first");
        secondState.Value.Should().Be("second");
    }

    private static IHost CreateHost(string value)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options.ExcludeDefault());
            monica.AddModuleSystem();
            var created = 0;
            var state = monica.Composition.GetOrAdd(() => { created++; return new ProbeState { Value = value }; });
            monica.Composition.GetOrAdd(() => { created++; return new ProbeState(); }).Should().BeSameAs(state);
            created.Should().Be(1);
        });
        return builder.Build();
    }

    internal sealed class ProbeState { internal string? Value { get; set; } }
    private sealed class UnregisteredState;
    internal sealed class PostMarker;
    internal sealed class BarrierMarker;
}

internal sealed class FinalizerProbeModule : MonicaModule<FinalizerProbeOptions>
{
    public override void ConfigureServices(ModuleContext<FinalizerProbeOptions> context)
    {
        context.Composition.Should().BeSameAs(Application.Composition);
        context.Composition.GetOrAdd(() => new MonicaCompositionStateTests.ProbeState()).Value.Should().Be("host-owned");
    }

    public override void PostConfigureServices(ModuleContext<FinalizerProbeOptions> context)
    {
        context.Services.AddSingleton<MonicaCompositionStateTests.PostMarker>();
        ScheduleStartupWork("finalizer-proof", static () => { },
            () => context.Services.AddSingleton<MonicaCompositionStateTests.BarrierMarker>(),
            ModuleStartupWorkBarrier.BeforeServiceRegistrationCompletion);
    }
}

internal sealed class FinalizerProbeOptions : ModuleOptions<FinalizerProbeModule>;
