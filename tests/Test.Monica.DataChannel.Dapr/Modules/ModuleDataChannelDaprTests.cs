using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapr.Client;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Abstractions.Partitioning;
using Monica.DataChannel.Dapr.Abstractions;
using Monica.DataChannel.Dapr.Providers;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Providers.Default;
using Monica.Modules;
using Monica.Testing.Hosting;
using NSubstitute;
using Xunit;

namespace Test.Monica.DataChannel.Dapr.Modules;

public sealed class ModuleDataChannelDaprTests
{
    [Fact]
    public async Task UseDaprBindings_WhenOutputIsSent_ShouldSelectDaprExplicitlyAndPreservePartitionMetadataAndCancellation()
    {
        var client = CreateClient();
        var factory = new BindingApplicationFactory(new BindingScenario());
        await using var application = await factory.CreateAsync(
            scenario => scenario.With(client), TestContext.Current.CancellationToken);
        Assert.Contains(application.ModuleSnapshots, module => module.ModuleType == typeof(ModuleDataChannelDapr));
        Assert.Contains(application.ModuleSnapshots, module => module.ModuleType == typeof(ModuleDaprClient));
        var channel = application.Services.GetRequiredService<IDataChannelManager>().Fetch("output")!;

        await channel.SendDataFromInnerAsync("order-42", TestContext.Current.CancellationToken);

        await client.Received(1).InvokeBindingAsync<object>(
            "orders-output", "create", "order-42",
            Arg.Is<IReadOnlyDictionary<string, string>>(metadata =>
                metadata[DataChannelPartitionConstants.MonicaPartitionKey] == "flight-42"
                && metadata[DataChannelPartitionConstants.PartitionKey] == "flight-42"
                && metadata[DataChannelPartitionConstants.MessageKey] == "flight-42"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Callback_WhenPipelineIsStillProcessing_ShouldAwaitCompletionBeforeReturningSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new BindingScenario
        {
            Receive = async context =>
            {
                Assert.Equal("order-42", Assert.IsType<JsonElement>(context.Data).GetProperty("id").GetString());
                Assert.True(context.CancellationToken.CanBeCanceled);
                Assert.Equal("/bindings/orders", ((IDictionary<string, object?>)context.Metadata)["Message.Receive.Route"]);
                entered.SetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            }
        };
        await using var application = await new BindingApplicationFactory(state).CreateAsync(
            scenario => scenario.With(CreateClient()), TestContext.Current.CancellationToken);
        using var client = ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

        var delivery = client.PostAsJsonAsync("/bindings/orders", new { id = "order-42" }, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(delivery.IsCompleted);
        release.SetResult();
        using var response = await delivery;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Callback_WhenDispatcherAcceptsDurably_ShouldSkipTheNormalPipeline()
    {
        var state = new BindingScenario { EnableDispatcher = true };
        var dispatcher = Substitute.For<IDaprBindingInputDispatcher>();
        dispatcher.TryDispatchAsync(Arg.Any<ChannelDataContext>(),
            Arg.Any<Func<ChannelDataContext, CancellationToken, Task>>(), Arg.Any<CancellationToken>()).Returns(true);
        await using var application = await new BindingApplicationFactory(state).CreateAsync(
            scenario => scenario.With(CreateClient()).With(dispatcher), TestContext.Current.CancellationToken);
        using var client = ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

        using var response = await client.PostAsJsonAsync("/bindings/orders", new { id = "order-42" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, state.ReceiveCount);
        await dispatcher.Received(1).TryDispatchAsync(Arg.Any<ChannelDataContext>(),
            Arg.Any<Func<ChannelDataContext, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_WhenDispatcherSuppliesALinkedContinuationToken_ShouldApplyItToTheDeliveredContext()
    {
        CancellationToken requestToken = default;
        CancellationToken continuationToken = default;
        var state = new BindingScenario
        {
            EnableDispatcher = true,
            Receive = context =>
            {
                Assert.Equal(continuationToken, context.CancellationToken);
                Assert.NotEqual(requestToken, context.CancellationToken);
                Assert.True(context.CancellationToken.CanBeCanceled);
                return Task.CompletedTask;
            }
        };
        var dispatcher = Substitute.For<IDaprBindingInputDispatcher>();
        dispatcher.TryDispatchAsync(Arg.Any<ChannelDataContext>(),
            Arg.Any<Func<ChannelDataContext, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async invocation =>
            {
                var incoming = invocation.ArgAt<ChannelDataContext>(0);
                var next = invocation.ArgAt<Func<ChannelDataContext, CancellationToken, Task>>(1);
                requestToken = invocation.ArgAt<CancellationToken>(2);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
                continuationToken = linked.Token;
                var replacement = new ChannelDataContext(incoming.Source, incoming.Data).CopyMetadata(incoming);
                await next(replacement, linked.Token);
                return true;
            });
        await using var application = await new BindingApplicationFactory(state).CreateAsync(
            scenario => scenario.With(CreateClient()).With(dispatcher), TestContext.Current.CancellationToken);
        using var client = ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

        using var response = await client.PostAsJsonAsync("/bindings/orders", new { id = "order-42" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, state.ReceiveCount);
    }

    [Fact]
    public async Task Callback_WhenTwoHostsUseTheSameRoute_ShouldKeepTheirPipelinesIndependent()
    {
        var first = new BindingScenario();
        var second = new BindingScenario();
        await using var firstApplication = await new BindingApplicationFactory(first).CreateAsync(
            scenario => scenario.With(CreateClient()), TestContext.Current.CancellationToken);
        await using var secondApplication = await new BindingApplicationFactory(second).CreateAsync(
            scenario => scenario.With(CreateClient()), TestContext.Current.CancellationToken);
        using var client = ((TestServer)secondApplication.Services.GetRequiredService<IServer>()).CreateClient();

        using var response = await client.PostAsJsonAsync("/bindings/orders", new { id = "second" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, first.ReceiveCount);
        Assert.Equal(1, second.ReceiveCount);
    }

    [Fact]
    public async Task Callback_WhenTwoPipelinesClaimTheSameRoute_ShouldRejectTheDuplicateDuringComposition()
    {
        var factory = new BindingApplicationFactory(new BindingScenario { DuplicateRoute = true });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(
            scenario => scenario.With(CreateClient()), TestContext.Current.CancellationToken));
        Assert.Contains("already owned by another data channel", failure.Message);
    }

    private static DaprClient CreateClient()
    {
        var client = Substitute.For<DaprClient>();
        client.CheckOutboundHealthAsync(Arg.Any<CancellationToken>()).Returns(true);
        client.InvokeBindingAsync<object>(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return client;
    }

    private sealed class BindingScenario
    {
        private int _receiveCount;
        public bool EnableDispatcher { get; init; }
        public bool DuplicateRoute { get; init; }
        public Func<ChannelDataContext, Task>? Receive { get; init; }
        public int ReceiveCount => Volatile.Read(ref _receiveCount);
        public async Task ReceiveAsync(ChannelDataContext data)
        {
            Interlocked.Increment(ref _receiveCount);
            if (Receive is not null) await Receive(data);
        }
    }

    private sealed class BindingApplicationFactory(BindingScenario state)
        : MonicaTestApplicationFactory<ModuleDataChannelDaprTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddDataChannel().UseDaprBindings().UseSetup<BindingSetup>();
        }

        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(state);
            services.AddSingleton<IDataChannelPartitionKeyResolver, PartitionKeyResolver>();
        }
    }

    private sealed class BindingSetup(BindingScenario state) : IDataChannelSetup
    {
        public void Setup(IDataChannelRegistrar channels)
        {
            channels.Add("input", pipeline => pipeline.SetOuterEndpoint(new DaprBindingOptions(EDaprBindingType.Kafka, ConnectionDirection.Input)
            {
                InputListenerRoute = "/bindings/orders", EnableInputDispatcher = state.EnableDispatcher
            }).SetInnerEndpoint<RecordingEndpoint>());
            channels.Add("output", pipeline => pipeline.SetOuterEndpoint(new DaprBindingOptions(EDaprBindingType.Kafka, ConnectionDirection.Output)
            {
                OutputBindingName = "orders-output"
            }));
            if (state.DuplicateRoute)
                channels.Add("duplicate", pipeline => pipeline.SetOuterEndpoint(new DaprBindingOptions(EDaprBindingType.Kafka, ConnectionDirection.Input)
                {
                    InputListenerRoute = "/bindings/orders"
                }));
        }
    }

    private sealed class RecordingEndpoint(BindingScenario state) : DefaultChannelEndpoint
    {
        public override Task ReceiveDataAsync(ChannelDataContext data) => state.ReceiveAsync(data);
    }

    private sealed class PartitionKeyResolver : IDataChannelPartitionKeyResolver
    {
        public ValueTask<string?> ResolvePartitionKeyAsync(ChannelDataContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("flight-42");
    }
}
