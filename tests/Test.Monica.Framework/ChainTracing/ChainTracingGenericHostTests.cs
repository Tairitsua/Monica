using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Exceptions;
using Monica.Core.Modularity.Extensions;
using Monica.Framework.ChainTracing.Abstractions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Framework.ChainTracing;

public sealed class ChainTracingGenericHostTests
{
    [Fact]
    public void Build_WhenUsingCoreChainTracing_ShouldRetainGenericHostServices()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddChainTracing();
        });

        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();

        host.Services.GetRequiredService<IChainTracing>().Should().NotBeNull();
        application.Modules.RuntimeSnapshots.Should().ContainSingle(snapshot =>
            snapshot.ModuleType == typeof(ModuleChainTracing)
            && snapshot.IsWebModule
            && !snapshot.RequiresWebHost);
    }

    [Fact]
    public void AddMonica_WhenActorInvocationTracingIsSelected_ShouldRejectGenericHostWithFeatureReason()
    {
        var builder = Host.CreateApplicationBuilder();

        Action compose = () => builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault());
            monica.AddChainTracing().UseActorInvocationTracing();
        });

        compose.Should().Throw<ModuleRegistrationException>()
            .WithMessage("*Actor invocation tracing middleware must run in an ASP.NET Core request pipeline.*");
    }
}
