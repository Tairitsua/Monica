using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Services.Support;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Modules;

namespace Monica.EventBus.Kafka.Providers.ConfluentKafka;

/// <summary>
/// Hosted service that owns Kafka consumers for active distributed EventBus subscriptions.
/// </summary>
internal sealed class KafkaEventBusSubscriptionHostedService(
    IEventSubscriptionRegistry subscriptionManager,
    IEventReceiveDispatcher dispatcher,
    EventMessageRetryPolicy retryPolicy,
    ITopicSubscriptionStatusStore topicStatusStore,
    IObservableInstanceRegistry observableManager,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    IKafkaClusterConfigProvider clusterConfigProvider,
    IOptions<ModuleEventBusKafkaOption> options,
    ILogger<KafkaEventBusSubscriptionHostedService> logger,
    string? serviceKey = null)
    : EventBusSubscriptionHostedServiceBase(
        subscriptionManager,
        dispatcher,
        topicStatusStore,
        observableManager,
        hostedServiceOptions,
        serviceScopeFactory,
        logger,
        serviceKey)
{
    private readonly TopicConsumerRegistry _consumers = new();

    /// <inheritdoc />
    public override string ServiceName => $"KafkaEventBus{(ServiceKey is null ? string.Empty : $"_{ServiceKey}")}";

    /// <inheritdoc />
    public override string? ServiceGroupId => nameof(ModuleEventBus);

    /// <inheritdoc />
    protected override EventBusProviderKind ProviderKind => EventBusProviderKind.Kafka;

    protected override Task CreateExternalSubscriptionForTopicAsync(string topicName, Type eventType, CancellationToken cancellationToken)
    {
        var consumer = new TopicConsumer(topicName, eventType, cancellationToken, ConsumeAsync);
        if (!_consumers.TryAddAndStart(consumer))
        {
            return consumer.StopAsync();
        }

        RecordState($"Started Kafka consumer for topic {topicName}", HostedServiceState.Running);
        return Task.CompletedTask;
    }

    protected override async Task RemoveExternalSubscriptionForTopicAsync(string topicName, CancellationToken cancellationToken)
    {
        var consumer = _consumers.Remove(topicName);
        if (consumer is null)
        {
            return;
        }

        await consumer.StopAsync();
    }

    protected override Task DisposeExternalSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var stopTasks = _consumers.Drain().Select(consumer => consumer.StopAsync());
        return Task.WhenAll(stopTasks);
    }

    protected override Task OnStoppingAsync(CancellationToken cancellationToken)
    {
        _consumers.StopAccepting();
        return base.OnStoppingAsync(cancellationToken);
    }

    private async Task ConsumeAsync(TopicConsumer topicConsumer)
    {
        try
        {
            await ConsumeTopicAsync(topicConsumer);
        }
        catch (OperationCanceledException) when (topicConsumer.CancellationToken.IsCancellationRequested)
        {
            // Expected during unsubscribe and shutdown.
        }
        catch (Exception ex)
        {
            TopicStatusStore.ReportState(
                ServiceKey, topicConsumer.TopicName, ProviderKind, TopicSubscriptionRuntimeState.Failed,
                $"Kafka consumer loop terminated for topic {topicConsumer.TopicName}; " +
                "the topic will not receive messages until the application restarts it",
                ex);

            RecordState($"Kafka consumer stopped for topic {topicConsumer.TopicName}", HostedServiceState.Degraded, ex);
        }
    }

    private async Task ConsumeTopicAsync(TopicConsumer topicConsumer)
    {
        var cancellationToken = topicConsumer.CancellationToken;
        var cluster = clusterConfigProvider.GetDirectEventBusCluster();
        var consumerConfig = KafkaClientConfigFactory.BuildConsumerConfig(cluster, options.Value, ServiceKey);
        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(topicConsumer.TopicName);

        // The consumer proves health only by delivering its first message; assignment events are
        // not observable through the Confluent consumer API used here.
        var hasProvenHealthy = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(cancellationToken);
                if (result?.Message is null)
                {
                    continue;
                }
                string? Header(string name)
                {
                    var value = result.Message.Headers?.LastOrDefault(header => header.Key == name);
                    return value is null ? null : Encoding.UTF8.GetString(value.GetValueBytes());
                }
                var metadata = new EventDeliveryMetadata(
                    Header("monica-message-id"),
                    Header("monica-source"),
                    Header("monica-event-name") ?? EventNameAttribute.GetNameOrDefault(topicConsumer.EventType),
                    topicConsumer.TopicName,
                    ServiceKey,
                    Header("traceparent"),
                    Header("tracestate"));
                var processed = await CommitAfterHandlingAsync(
                    () => result.Message.Value is null
                        ? Task.FromException(new EventMessageDeserializationException(
                            $"Kafka message on topic '{topicConsumer.TopicName}' has a null body."))
                        : HandleExternalMessageAsync(
                            new EventMessage(metadata, EventSubscriptionScope.Distributed,
                                Encoding.UTF8.GetBytes(result.Message.Value)),
                            cancellationToken),
                    () => consumer.Commit(result),
                    () => consumer.Seek(result.TopicPartitionOffset),
                    failure =>
                    {
                        TopicStatusStore.ReportError(ServiceKey, topicConsumer.TopicName,
                            $"Retry budget exhausted for Kafka message {metadata.MessageId} at {result.TopicPartitionOffset}",
                            failure);
                        RecordState($"Retry budget exhausted for Kafka message for topic {topicConsumer.TopicName}",
                            HostedServiceState.Degraded, failure);
                        Logger.LogError(failure,
                            "Retry budget exhausted for Kafka message {MessageId} at {TopicPartitionOffset}; skipping delivery " +
                            "(ServiceKey: {ServiceKey})",
                            metadata.MessageId, result.TopicPartitionOffset, ServiceKey ?? "default");
                    },
                    retryPolicy, cancellationToken);

                if (processed)
                {
                    TopicStatusStore.ReportMessageProcessed(ServiceKey, topicConsumer.TopicName);
                }

                if (!hasProvenHealthy)
                {
                    hasProvenHealthy = true;
                    TopicStatusStore.ReportState(
                        ServiceKey, topicConsumer.TopicName, ProviderKind, TopicSubscriptionRuntimeState.Healthy,
                        $"Kafka consumer is delivering messages for topic {topicConsumer.TopicName}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (KafkaRewindFailedException)
            {
                // After a failed seek, this consumer cannot safely commit any later offset.
                throw;
            }
            catch (Exception ex)
            {
                TopicStatusStore.ReportState(
                    ServiceKey, topicConsumer.TopicName, ProviderKind, TopicSubscriptionRuntimeState.Recovering,
                    $"Kafka consumer failed for topic {topicConsumer.TopicName}; retrying after backoff",
                    ex);

                RecordState($"Kafka consumer failed for topic {topicConsumer.TopicName}", HostedServiceState.Degraded, ex);
                try
                {
                    await Task.Delay(options.Value.ConsumerErrorBackoff, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Consumer.Close enters a librdkafka LeaveGroup path that can dereference a missing
        // coordinator. Consumer.Dispose uses NO_CONSUMER_CLOSE instead.
    }

    internal static async Task<bool> CommitAfterHandlingAsync(
        Func<Task> handle, Action commit, Action rewind, Action<Exception> reject,
        EventMessageRetryPolicy retryPolicy,
        CancellationToken cancellationToken = default)
    {
        var processed = true;
        try
        {
            try
            {
                await retryPolicy.ExecuteAsync(handle, cancellationToken);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                // End exhausted processing only after the last attempt's transaction and scope unwind.
                cancellationToken.ThrowIfCancellationRequested();
                reject(failure);
                processed = false;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disposes this consumer without advancing its broker position.
            throw;
        }
        catch (Exception failure)
        {
            // Failure to record the terminal disposition must retain the same delivery identity.
            RewindOrThrow(rewind, failure);
            throw;
        }

        try
        {
            commit();
        }
        catch (Exception failure)
        {
            RewindOrThrow(rewind, failure);
            throw;
        }

        return processed;
    }

    private static void RewindOrThrow(Action rewind, Exception? originalFailure)
    {
        try
        {
            rewind();
        }
        catch (Exception seekFailure)
        {
            throw new KafkaRewindFailedException(
                "Kafka failed to rewind an unacknowledged message; the consumer must stop.",
                originalFailure is null ? seekFailure : new AggregateException(originalFailure, seekFailure));
        }
    }

    private sealed class KafkaRewindFailedException(string message, Exception innerException)
        : Exception(message, innerException)
    {
    }

    /// <summary>
    /// Owns publication and draining of topic consumers across service lifecycle transitions.
    /// </summary>
    internal sealed class TopicConsumerRegistry
    {
        private readonly object _lifecycleLock = new();
        private readonly Dictionary<string, TopicConsumer> _consumers = new(StringComparer.Ordinal);
        private bool _isAccepting = true;

        internal bool TryAddAndStart(TopicConsumer consumer)
        {
            lock (_lifecycleLock)
            {
                if (!_isAccepting || !_consumers.TryAdd(consumer.TopicName, consumer))
                {
                    return false;
                }

                if (consumer.TryStart())
                {
                    return true;
                }

                _consumers.Remove(consumer.TopicName);
                return false;
            }
        }

        internal TopicConsumer? Remove(string topicName)
        {
            lock (_lifecycleLock)
            {
                return _consumers.Remove(topicName, out var consumer)
                    ? consumer
                    : null;
            }
        }

        internal IReadOnlyList<TopicConsumer> Drain()
        {
            lock (_lifecycleLock)
            {
                var consumers = _consumers.Values.ToList();
                _consumers.Clear();
                return consumers;
            }
        }

        internal void StopAccepting()
        {
            lock (_lifecycleLock)
            {
                _isAccepting = false;
            }
        }
    }

    /// <summary>
    /// Owns the execution and cancellation lifetime of one topic consumer.
    /// </summary>
    internal sealed class TopicConsumer
    {
        private readonly object _lifecycleLock = new();
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Func<TopicConsumer, Task> _consumeAsync;
        private Task? _consumerTask;
        private Task? _stopTask;

        internal TopicConsumer(
            string topicName,
            Type eventType,
            CancellationToken cancellationToken,
            Func<TopicConsumer, Task> consumeAsync)
        {
            TopicName = topicName;
            EventType = eventType;
            _consumeAsync = consumeAsync;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken = _cancellationTokenSource.Token;
        }

        public string TopicName { get; }

        public Type EventType { get; }

        public CancellationToken CancellationToken { get; }

        internal bool TryStart()
        {
            lock (_lifecycleLock)
            {
                if (_consumerTask is not null || _stopTask is not null)
                {
                    return false;
                }

                _consumerTask = Task.Factory.StartNew(
                    () => _consumeAsync(this),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).Unwrap();
                return true;
            }
        }

        internal Task StopAsync()
        {
            lock (_lifecycleLock)
            {
                // Registry publication can race with removal, so shutdown must capture startup atomically.
                return _stopTask ??= StopCoreAsync(_consumerTask);
            }
        }

        private async Task StopCoreAsync(Task? consumerTask)
        {
            try
            {
                await _cancellationTokenSource.CancelAsync();
            }
            finally
            {
                try
                {
                    if (consumerTask is not null)
                    {
                        await consumerTask;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected during unsubscribe and shutdown.
                }
                finally
                {
                    _cancellationTokenSource.Dispose();
                }
            }
        }
    }
}
