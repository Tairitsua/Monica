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
    : DistributedEventBusBase(serviceScopeFactory, eventHandlerInvoker, subscriptionManager, loggerFactory, serviceKey), IEventBatchTransport, IDisposable
{
    // Starting a produce request and disposing the native handle must be mutually exclusive. Once
    // queued, librdkafka owns the delivery and Flush drains it during shutdown.
    private readonly object _producerLifetimeLock = new();
    // Canceled only after native disposal, never by a publishing caller or an Outbox deadline.
    private readonly CancellationTokenSource _producerStopped = new();
    private readonly Lazy<IProducer<string, string>> _producer = new(() =>
    {
        var cluster = clusterConfigProvider.GetDirectEventBusCluster();
        var config = KafkaClientConfigFactory.BuildProducerConfig(cluster, options.Value);
        return new ProducerBuilder<string, string>(config).Build();
    });
    private bool _isDisposed;

    /// <inheritdoc />
    public EventBatchLimits BatchLimits { get; } = options.Value.PublishBatch
        ?? throw new ArgumentException("Kafka publish batch limits are required.", nameof(options));

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
        var record = PrepareRecord(message);
        await StartProduce(message.Metadata.TopicName, record, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventSendResult>> SendBatchAsync(IReadOnlyList<EventMessage> messages,
        CancellationToken cancellationToken)
    {
        BatchLimits.Validate(messages);
        // Validate and prepare every record before admitting the first native send.
        var records = messages.Select(PrepareRecord).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var sends = new Task<EventSendResult>[records.Length];
        for (var index = 0; index < records.Length; index++)
        {
            try
            {
                // Queue the bounded group before awaiting acknowledgments; librdkafka batches broker requests.
                sends[index] = ObserveDeliveryAsync(StartProduce(messages[index].Metadata.TopicName,
                    records[index], cancellationToken));
            }
            catch (Exception exception)
            {
                // Admission failed: retain other admitted sends and their independent results.
                sends[index] = Task.FromResult(EventSendResult.Failed(exception));
            }
        }
        return await Task.WhenAll(sends);
    }

    private static async Task<EventSendResult> ObserveDeliveryAsync(Task<DeliveryResult<string, string>> delivery)
    {
        try
        {
            await delivery;
            return EventSendResult.Accepted;
        }
        catch (ProduceException<string, string> exception)
            when (exception.DeliveryResult.Status == PersistenceStatus.NotPersisted)
        {
            return EventSendResult.Failed(exception);
        }
        catch (Exception exception)
        {
            return EventSendResult.Unknown(exception);
        }
    }

    private Message<string, string> PrepareRecord(EventMessage message)
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

        return new Message<string, string>
        {
            Key = metadata.TransportKey ?? metadata.EventName,
            Value = Encoding.UTF8.GetString(message.Body.Span),
            Headers = headers
        };
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
        await EventTransportBatch.PublishAsync(this, messages, cancellationToken);
    }

    private Task<DeliveryResult<string, string>> StartProduce(
        string topicName,
        Message<string, string> message,
        CancellationToken cancellationToken)
    {
        lock (_producerLifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            // Request cancellation cancels only the native API's managed waiter, so do not pass it through.
            // Disposal is different: once the handle is destroyed, missing reports can no longer arrive.
            var producer = _producer.Value;
            cancellationToken.ThrowIfCancellationRequested();
            return producer.ProduceAsync(topicName, message, _producerStopped.Token);
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
                _producerStopped.Dispose();
                return;
            }

            try
            {
                _producer.Value.Flush(options.Value.ProducerFlushTimeout);
            }
            finally
            {
                try
                {
                    _producer.Value.Dispose();
                    // Flush can time out. Confluent suppresses delivery callbacks after handle destruction;
                    // unblock those waiters as unknown only after native sending has actually stopped.
                    _producerStopped.Cancel();
                }
                finally { _producerStopped.Dispose(); }
            }
        }
    }
}
