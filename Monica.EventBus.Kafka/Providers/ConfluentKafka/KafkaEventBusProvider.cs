using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Services.Support;
using Monica.EventBus.Services.Support;
using Monica.EventBus.Models;
using Monica.Modules;

namespace Monica.EventBus.Kafka.Providers.ConfluentKafka;

/// <summary>
/// Native Kafka distributed EventBus provider.
/// </summary>
/// <param name="serviceScopeFactory">Creates scopes for event handlers.</param>
/// <param name="eventHandlerInvoker">Invokes resolved event handlers.</param>
/// <param name="subscriptionManager">Owns this host's subscription catalog.</param>
/// <param name="clusterConfigProvider">Provides Kafka cluster settings.</param>
/// <param name="messageFactory">Captures stable metadata and the JSON payload.</param>
/// <param name="options">Provides Kafka event bus options.</param>
/// <param name="loggerFactory">Creates the event bus logger.</param>
/// <param name="serviceKey">An optional keyed-provider identifier.</param>
public sealed class KafkaEventBusProvider(
    IServiceScopeFactory serviceScopeFactory,
    IEventHandlerInvoker eventHandlerInvoker,
    IEventSubscriptionRegistry subscriptionManager,
    IKafkaClusterConfigProvider clusterConfigProvider,
    IEventMessageFactory messageFactory,
    IOptions<ModuleEventBusKafkaOption> options,
    ILoggerFactory loggerFactory,
    string? serviceKey = null)
    : DistributedEventBusBase(serviceScopeFactory, eventHandlerInvoker, subscriptionManager, loggerFactory, serviceKey), IEventTransport, IDisposable
{
    // Starting a produce request and disposing the native handle must be mutually exclusive. Once
    // queued, librdkafka owns the delivery and Flush drains it during shutdown.
    private readonly object _producerLifetimeLock = new();
    private readonly Lazy<IProducer<string, string>> _producer = new(() =>
    {
        var cluster = clusterConfigProvider.GetDirectEventBusCluster();
        var config = KafkaClientConfigFactory.BuildProducerConfig(cluster, options.Value);
        return new ProducerBuilder<string, string>(config).Build();
    });
    private bool _isDisposed;

    internal KafkaEventBusProvider(
        IServiceScopeFactory serviceScopeFactory,
        IEventHandlerInvoker eventHandlerInvoker,
        IEventSubscriptionRegistry subscriptionManager,
        IKafkaClusterConfigProvider clusterConfigProvider,
        IEventMessageFactory messageFactory,
        IOptions<ModuleEventBusKafkaOption> options,
        ILoggerFactory loggerFactory,
        Func<ProducerConfig, IProducer<string, string>> producerFactory,
        string? serviceKey = null)
        : this(serviceScopeFactory, eventHandlerInvoker, subscriptionManager, clusterConfigProvider,
            messageFactory, options, loggerFactory, serviceKey)
    {
        ArgumentNullException.ThrowIfNull(producerFactory);
        _producer = new(() => producerFactory(KafkaClientConfigFactory.BuildProducerConfig(
            clusterConfigProvider.GetDirectEventBusCluster(), options.Value)));
    }

    /// <inheritdoc />
    public async Task SendAsync(EventMessage message, CancellationToken cancellationToken)
    {
        if (message.Scope != EventSubscriptionScope.Distributed || message.Metadata.ServiceKey != ServiceKey)
        {
            throw new InvalidOperationException("The prepared event does not target this Kafka transport.");
        }

        var metadata = message.Metadata;
        if (string.IsNullOrWhiteSpace(metadata.MessageId) || string.IsNullOrWhiteSpace(metadata.Source))
        {
            throw new InvalidOperationException("A durable Kafka event requires a stable ID and source.");
        }

        var headers = new Headers
        {
            { "monica-message-id", Encoding.UTF8.GetBytes(metadata.MessageId) },
            { "monica-source", Encoding.UTF8.GetBytes(metadata.Source) },
            { "monica-event-name", Encoding.UTF8.GetBytes(metadata.EventName) }
        };
        if (metadata.TraceParent is not null)
        {
            headers.Add("traceparent", Encoding.UTF8.GetBytes(metadata.TraceParent));
        }
        if (metadata.TraceState is not null)
        {
            headers.Add("tracestate", Encoding.UTF8.GetBytes(metadata.TraceState));
        }

        await StartProduce(metadata.TopicName,
            metadata.TransportKey ?? metadata.EventName,
            Encoding.UTF8.GetString(message.Body.Span), headers, cancellationToken);
    }

    /// <inheritdoc />
    public override async Task PublishAsync(Type eventType, object eventData, string? topicName = null, CancellationToken cancellationToken = default)
    {
        await SendAsync(messageFactory.Prepare(eventType, eventData,
            EventSubscriptionScope.Distributed, topicName, ServiceKey), cancellationToken);
    }

    /// <inheritdoc />
    public override async Task BulkPublishAsync(Type eventType, IEnumerable<object> eventDataList, string? topicName = null, CancellationToken cancellationToken = default)
    {
        var messages = eventDataList.Select(eventData => messageFactory.Prepare(
            eventType, eventData, EventSubscriptionScope.Distributed, topicName, ServiceKey)).ToArray();
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendAsync(message, cancellationToken);
        }
    }

    private Task<DeliveryResult<string, string>> StartProduce(
        string topicName,
        string key,
        string payload,
        Headers? headers,
        CancellationToken cancellationToken)
    {
        lock (_producerLifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return _producer.Value.ProduceAsync(
                topicName,
                new Message<string, string>
                {
                    Key = key,
                    Value = payload,
                    Headers = headers
                },
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_producerLifetimeLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (!_producer.IsValueCreated)
            {
                return;
            }

            try
            {
                _producer.Value.Flush(options.Value.ProducerFlushTimeout);
            }
            finally
            {
                _producer.Value.Dispose();
            }
        }
    }
}
