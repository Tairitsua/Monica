using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Providers.Default;
using Monica.Modules;
using Monica.Testing.Hosting;
using Test.Monica.DataChannel.Support;
using Xunit;

namespace Test.Monica.DataChannel.Modules;

public sealed class ModuleDataChannelTests
{
    [Fact]
    public async Task UseSetup_WhenSeveralContributorsAreRegistered_ShouldRunEachTypeOnceInOrderBeforeMaterialization()
    {
        var scenario = new SetupCompositionScenario();
        await using var application = await new SetupCompositionApplicationFactory(scenario)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        scenario.SetupCalls.Should().Equal("first", "second");
        scenario.MaterializedChannelCountsAtSetup.Should().Equal(0, 0);
        var channels = application.Services.GetRequiredService<IDataChannelManager>().FetchAll();
        channels.Select(channel => channel.Id).Should().BeEquivalentTo(new[] { "first", "second" });
        channels.Should().OnlyContain(channel => channel.Pipe.IsInitialized);
    }

    [Fact]
    public async Task UseSetup_WhenContributorsRepeatAChannelId_ShouldRejectBeforeMaterialization()
    {
        var scenario = new SetupCompositionScenario { DuplicateChannelId = true };
        var create = () => new SetupCompositionApplicationFactory(scenario)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);

        await create.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Data channel 'first' is already registered in the current host.");
        scenario.SetupCalls.Should().Equal("first", "second");
        scenario.MaterializedChannelCountsAtSetup.Should().Equal(0, 0);
    }

    [Fact]
    public async Task AddDataChannel_WhenOnlySetupAndMinimalApiAreSelected_ShouldExecuteProductionResultProjection()
    {
        var scenario = new DataChannelScenario();
        await using var application = await new DataChannelTestApplicationFactory(scenario, enableMinimalApi: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        application.Services.GetRequiredService<IResultErrorMessageProvider>().Should().NotBeNull();
        using var client = ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

        using var response = await client.GetAsync("/channels", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var options = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var envelope = JsonSerializer.Deserialize<Res<JsonElement>>(body, options)!;
        envelope.Status.Should().Be(ResStatus.Ok);
        envelope.Message.Should().BeNullOrEmpty();
        envelope.Data.ValueKind.Should().Be(JsonValueKind.Array);
        envelope.Data.GetArrayLength().Should().Be(1);
        var channel = envelope.Data[0];
        channel.GetProperty("id").GetString().Should().Be(scenario.Primary.Id);
        channel.GetProperty("groupId").GetString().Should().Be(scenario.Primary.GroupId);
        channel.GetProperty("isInitialized").GetBoolean().Should().BeTrue();
        channel.GetProperty("outerEndpoint").GetProperty("metadata").GetProperty("json").GetString().Should().NotBeNullOrEmpty();
    }

    private sealed class SetupCompositionScenario
    {
        public bool DuplicateChannelId { get; init; }
        public List<string> SetupCalls { get; } = [];
        public List<int> MaterializedChannelCountsAtSetup { get; } = [];
    }

    private sealed class FirstChannelSetup(SetupCompositionScenario scenario, IDataChannelManager manager)
        : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar channels)
        {
            scenario.SetupCalls.Add("first");
            scenario.MaterializedChannelCountsAtSetup.Add(manager.FetchAll().Count);
            channels.Add("first", pipeline => pipeline.SetOuterEndpoint(new DefaultEndpointOptions()));
        }
    }

    private sealed class SecondChannelSetup(SetupCompositionScenario scenario, IDataChannelManager manager)
        : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar channels)
        {
            scenario.SetupCalls.Add("second");
            scenario.MaterializedChannelCountsAtSetup.Add(manager.FetchAll().Count);
            channels.Add(scenario.DuplicateChannelId ? "first" : "second",
                pipeline => pipeline.SetOuterEndpoint(new DefaultEndpointOptions()));
        }
    }

    private sealed class SetupCompositionApplicationFactory(SetupCompositionScenario scenario)
        : MonicaTestApplicationFactory<ModuleDataChannelTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddDataChannel()
                .UseSetup<FirstChannelSetup>()
                .UseSetup<SecondChannelSetup>()
                .UseSetup<FirstChannelSetup>();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(scenario);
        }
    }
}
