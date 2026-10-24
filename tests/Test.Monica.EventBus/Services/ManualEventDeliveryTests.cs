using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core;
using Monica.Core.Modularity.Extensions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class ManualEventDeliveryTests
{
    [Fact]
    public async Task ManualHost_KeepsSubscriptionsHeldAndDeliversOnlySelectedHandlersThroughOwnedScopes()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Application:AutonomousWork"] = "false";
        var recorder = new Recorder();
        builder.Services.AddSingleton(recorder);
        builder.Services.AddScoped<SelectedHandler>();
        builder.Services.AddScoped<OtherHandler>();
        builder.Services.AddHostedService<ForbiddenTransport>();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => { options.ExcludeDefault(); options.Add(typeof(ManualEventDeliveryTests).Assembly); });
            monica.AddEventBus().UseNoOpDistributedEventBus();
        });
        using var host = builder.Build();
        var token = TestContext.Current.CancellationToken;
        await host.StartAsync(token);
        var registry = host.Services.GetRequiredService<IEventSubscriptionRegistry>();
        var targets = registry.GetByEventType(typeof(TestNotice));
        Assert.Equal(2, targets.Count);
        Assert.All(targets, subscription => Assert.Equal(EventSubscriptionState.Held, subscription.State));
        Assert.Equal(0, recorder.Constructions);
        var selected = targets.Single(subscription => subscription.HandlerType == typeof(SelectedHandler));
        var dispatcher = host.Services.GetRequiredService<IEventReceiveDispatcher>();
        var message = host.Services.GetRequiredService<IEventMessageFactory>()
            .Prepare(typeof(TestNotice), new TestNotice("manual"), EventSubscriptionScope.Distributed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(message, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ActivateAsync(selected.Id, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => selected.ActivateAsync());
        await dispatcher.DispatchTestAsync(message, [selected.Id], token);

        Assert.Equal([message.Metadata.MessageId], recorder.MessageIds);
        Assert.Equal(1, recorder.Constructions);
        Assert.Equal(1, recorder.Disposals);
        Assert.Equal(0, recorder.OtherDeliveries);
        Assert.All(targets, subscription => Assert.Equal(EventSubscriptionState.Held, subscription.State));
        Assert.Contains(host.Services.GetRequiredService<MonicaApplication>().ActivationResources,
            resource => resource.ImplementationType == typeof(ForbiddenTransport) && resource.Mode == ActivationMode.Manual);

        // Validate the entire selection before invoking even its first eligible handler.
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchTestAsync(message,
            [selected.Id, EventSubscriptionId.NewId()], token));
        Assert.Single(recorder.MessageIds);
        await dispatcher.DispatchTestAsync(message, [selected.Id], token);
        Assert.Equal([message.Metadata.MessageId, message.Metadata.MessageId], recorder.MessageIds);
        Assert.Equal(2, recorder.Disposals);
        await host.StopAsync(token);
    }

    public sealed record TestNotice(string Value);

    public sealed class Recorder
    {
        public int Constructions;
        public int Disposals;
        public int OtherDeliveries;
        public List<string?> MessageIds { get; } = [];
    }

    public sealed class SelectedHandler : IDistributedEventHandler<TestNotice>, IAsyncDisposable
    {
        private readonly Recorder _recorder;
        private readonly IEventDeliveryContext _delivery;
        public SelectedHandler(Recorder recorder, IEventDeliveryContext delivery)
        {
            _recorder = recorder;
            _delivery = delivery;
            recorder.Constructions++;
        }
        public Task HandleEventAsync(TestNotice eventData, CancellationToken cancellationToken)
        {
            _recorder.MessageIds.Add(_delivery.Current!.MessageId);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { _recorder.Disposals++; return ValueTask.CompletedTask; }
    }

    public sealed class OtherHandler(Recorder recorder) : IDistributedEventHandler<TestNotice>
    {
        public Task HandleEventAsync(TestNotice eventData, CancellationToken cancellationToken)
        { recorder.OtherDeliveries++; return Task.CompletedTask; }
    }

    private sealed class ForbiddenTransport : IHostedService
    {
        public ForbiddenTransport() => throw new InvalidOperationException("Manual composition must not construct a transport worker.");
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
