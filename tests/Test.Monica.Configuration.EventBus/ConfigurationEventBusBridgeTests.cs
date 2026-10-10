using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.EventBus.Modules;
using Monica.Configuration.EventBus.Services;
using Monica.Configuration.Models;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Models;
using Monica.EventBus.Services;
using Xunit;

namespace Test.Monica.Configuration.EventBus;

public sealed class ConfigurationEventBusBridgeTests
{
    [Fact]
    public async Task NotifyAsync_ShouldPublishConfigurationNotificationToConfiguredTopic()
    {
        var eventBus = new RecordingDistributedEventBus();
        var services = new ServiceCollection()
            .AddSingleton<IDistributedEventBus>(eventBus)
            .BuildServiceProvider();
        var notifier = new ConfigurationEventBusChangeNotifier(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleConfigurationEventBusOption
            {
                TopicName = "custom.configuration.reload"
            }));
        var notification = CreateNotification();

        await notifier.NotifyAsync(notification, TestContext.Current.CancellationToken);

        eventBus.PublishedEvents.Should().ContainSingle();
        eventBus.PublishedEvents[0].TopicName.Should().Be("custom.configuration.reload");
        eventBus.PublishedEvents[0].EventData.Should().Be(notification);
    }

    [Fact]
    public async Task NotifyAsync_WhenServiceKeyIsConfigured_ShouldUseKeyedDistributedEventBus()
    {
        var defaultEventBus = new RecordingDistributedEventBus();
        var keyedEventBus = new RecordingDistributedEventBus();
        var services = new ServiceCollection()
            .AddSingleton<IDistributedEventBus>(defaultEventBus)
            .AddKeyedSingleton<IDistributedEventBus>("configuration-reload", keyedEventBus)
            .BuildServiceProvider();
        var notifier = new ConfigurationEventBusChangeNotifier(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleConfigurationEventBusOption
            {
                DistributedEventBusServiceKey = "configuration-reload",
                TopicName = "custom.configuration.reload"
            }));
        var notification = CreateNotification();

        await notifier.NotifyAsync(notification, TestContext.Current.CancellationToken);

        defaultEventBus.PublishedEvents.Should().BeEmpty();
        keyedEventBus.PublishedEvents.Should().ContainSingle();
        keyedEventBus.PublishedEvents[0].TopicName.Should().Be("custom.configuration.reload");
        keyedEventBus.PublishedEvents[0].EventData.Should().Be(notification);
    }

    [Fact]
    public async Task StartAsync_ShouldSubscribeAndForwardNotificationsToReceiver()
    {
        var eventBus = new RecordingDistributedEventBus();
        var receiver = new RecordingReloadSignalReceiver();
        var services = new ServiceCollection()
            .AddSingleton<IDistributedEventBus>(eventBus)
            .BuildServiceProvider();
        var hostedService = new ConfigurationEventBusSubscriptionHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleConfigurationEventBusOption
            {
                TopicName = "custom.configuration.reload"
            }),
            receiver);
        var notification = CreateNotification();

        await hostedService.StartAsync(TestContext.Current.CancellationToken);
        await eventBus.PublishAsync(notification, "custom.configuration.reload", TestContext.Current.CancellationToken);

        receiver.ReceivedNotifications.Should().ContainSingle().Which.Should().Be(notification);
        receiver.ReceivedCancellationTokens.Should().ContainSingle().Which.Should().Be(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_WhenServiceKeyIsConfigured_ShouldSubscribeToKeyedDistributedEventBus()
    {
        var defaultEventBus = new RecordingDistributedEventBus();
        var keyedEventBus = new RecordingDistributedEventBus();
        var receiver = new RecordingReloadSignalReceiver();
        var services = new ServiceCollection()
            .AddSingleton<IDistributedEventBus>(defaultEventBus)
            .AddKeyedSingleton<IDistributedEventBus>("configuration-reload", keyedEventBus)
            .BuildServiceProvider();
        var hostedService = new ConfigurationEventBusSubscriptionHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleConfigurationEventBusOption
            {
                DistributedEventBusServiceKey = "configuration-reload",
                TopicName = "custom.configuration.reload"
            }),
            receiver);
        var notification = CreateNotification();

        await hostedService.StartAsync(TestContext.Current.CancellationToken);
        await defaultEventBus.PublishAsync(notification, "custom.configuration.reload", TestContext.Current.CancellationToken);
        await keyedEventBus.PublishAsync(notification, "custom.configuration.reload", TestContext.Current.CancellationToken);

        receiver.ReceivedNotifications.Should().ContainSingle().Which.Should().Be(notification);
    }

    [Fact]
    public async Task StartAsync_WhenDistributedEventBusIsMissing_ShouldFailFast()
    {
        var hostedService = new ConfigurationEventBusSubscriptionHostedService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ModuleConfigurationEventBusOption()),
            new RecordingReloadSignalReceiver());

        var act = () => hostedService.StartAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*IDistributedEventBus*");
    }

    private static ConfigurationReloadSignal CreateNotification()
    {
        return new ConfigurationReloadSignal
        {
            SignalId = Guid.NewGuid().ToString("N"),
            OriginInstanceId = "remote",
            StoreKey = "file:default",
            Kind = ConfigurationReloadSignalKind.DefinitionsChanged,
            Definitions =
            [
                new ConfigurationReloadDefinitionVersion
                {
                    DefinitionKey = "Demo.Options",
                    Version = 3
                }
            ],
            ChangedTime = DateTimeOffset.UtcNow
        };
    }

    private sealed class RecordingReloadSignalReceiver : IConfigurationReloadSignalReceiver
    {
        private readonly List<ConfigurationReloadSignal> _receivedNotifications = [];
        private readonly List<CancellationToken> _receivedCancellationTokens = [];

        public IReadOnlyList<ConfigurationReloadSignal> ReceivedNotifications => _receivedNotifications;
        public IReadOnlyList<CancellationToken> ReceivedCancellationTokens => _receivedCancellationTokens;

        public Task ReceiveAsync(ConfigurationReloadSignal notification, CancellationToken cancellationToken)
        {
            _receivedNotifications.Add(notification);
            _receivedCancellationTokens.Add(cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDistributedEventBus : IDistributedEventBus
    {
        public List<PublishedEvent> PublishedEvents { get; } = [];

        public IEventSubscriptionRegistry Subscriptions { get; } =
            new EventSubscriptionRegistry(NullLogger<EventSubscriptionRegistry>.Instance);

        public async Task PublishAsync<TEvent>(TEvent eventData, string? topicName = null, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            PublishedEvents.Add(new PublishedEvent(typeof(TEvent), eventData, topicName));
            foreach (var subscription in Subscriptions.GetByEventType(typeof(TEvent))
                         .Where(subscription => subscription.TopicName == topicName &&
                                                subscription.State == EventSubscriptionState.Active))
            {
                await using var scope = await subscription.HandlerFactory.CreateExecutionScopeAsync();
                await ((IDistributedEventHandler<TEvent>)scope.EventHandler)
                    .HandleEventAsync(eventData, cancellationToken);
            }
        }

        public Task BulkPublishAsync<TEvent>(IEnumerable<TEvent> eventDataList, string? topicName = null, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            throw new NotSupportedException();
        }

        public Task<IEventSubscription> SubscribeAsync<TEvent, THandler>(string? topicName = null)
            where TEvent : class
            where THandler : IEventHandler
        {
            throw new NotSupportedException();
        }

        public Task<IEventSubscription> SubscribeAsync<TEvent>(
            Func<TEvent, CancellationToken, Task> handler,
            string? topicName = null)
            where TEvent : class
        {
            throw new NotSupportedException();
        }

        public Task PublishAsync(Type eventType, object eventData, string? topicName = null, CancellationToken cancellationToken = default)
        {
            PublishedEvents.Add(new PublishedEvent(eventType, eventData, topicName));
            return Task.CompletedTask;
        }

        public Task BulkPublishAsync(Type eventType, IEnumerable<object> eventDataList, string? topicName = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed record PublishedEvent(Type EventType, object EventData, string? TopicName);

}
