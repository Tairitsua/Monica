using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.EventBus.Abstractions;
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

    protected override bool GroupSubscriptionsByWorkloadOwner => true;

    protected override Task CreateExternalSubscriptionForTopicAsync(SubscriptionGroup group, Type eventType, CancellationToken cancellationToken)
    {
        var consumer = new TopicConsumer(group.TopicName, cancellationToken, ConsumeAsync, group.WorkloadOwner);
        if (!_consumers.TryAddAndStart(consumer))
        {
            return consumer.StopAsync();
        }

        RecordState($"Started Kafka consumer for topic {group.TopicName}, owner {group.WorkloadOwner ?? "<unowned>"}", HostedServiceState.Running);
        return Task.CompletedTask;
    }

    protected override async Task RemoveExternalSubscriptionForTopicAsync(SubscriptionGroup group, CancellationToken cancellationToken)
    {
        var consumer = _consumers.Remove(group.TopicName, group.WorkloadOwner);
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
        var group = new SubscriptionGroup(topicConsumer.TopicName, topicConsumer.WorkloadOwner);
        try
        {
            while (!topicConsumer.CancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ConsumeTopicAsync(topicConsumer);
                    return;
                }
                catch (KafkaConsumerRestartRequiredException ex)
                {
                    // Discard an unsafe client without acknowledging a later offset. A fresh member
                    // resumes from the broker's last committed position, including after rebalances.
                    ReportSubscriptionState(group, TopicSubscriptionRuntimeState.Recovering,
                        "Kafka consumer ownership or position changed; recreating the consumer", ex);
                    await Task.Delay(options.Value.ConsumerErrorBackoff, topicConsumer.CancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (topicConsumer.CancellationToken.IsCancellationRequested)
        {
            // Expected during unsubscribe and shutdown.
        }
        catch (Exception ex)
        {
            ReportSubscriptionState(
                group, TopicSubscriptionRuntimeState.Failed,
                $"Kafka consumer loop terminated for topic {topicConsumer.TopicName}; " +
                "the topic will not receive messages until the application restarts it",
                ex);

            RecordState($"Kafka consumer stopped for topic {topicConsumer.TopicName}", HostedServiceState.Degraded, ex);
        }
    }

    private async Task ConsumeTopicAsync(TopicConsumer topicConsumer)
    {
        var cancellationToken = topicConsumer.CancellationToken;
        var group = new SubscriptionGroup(topicConsumer.TopicName, topicConsumer.WorkloadOwner);
        var cluster = clusterConfigProvider.GetDirectEventBusCluster();
        var consumerConfig = KafkaClientConfigFactory.BuildConsumerConfig(cluster, options.Value, ServiceKey, topicConsumer.WorkloadOwner);
        var hasProvenHealthy = false;
        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                hasProvenHealthy = true;
                ReportSubscriptionState(group, TopicSubscriptionRuntimeState.Healthy,
                    $"Kafka group '{consumerConfig.GroupId}' assigned {partitions.Count} partition(s)");
            })
            .SetPartitionsRevokedHandler((_, _) =>
            {
                hasProvenHealthy = false;
                ReportSubscriptionState(group, TopicSubscriptionRuntimeState.Subscribing,
                    $"Kafka group '{consumerConfig.GroupId}' is rebalancing");
            })
            .SetPartitionsLostHandler((_, _) =>
            {
                hasProvenHealthy = false;
                ReportSubscriptionState(group, TopicSubscriptionRuntimeState.Recovering,
                    $"Kafka group '{consumerConfig.GroupId}' lost partition ownership");
            })
            .Build();
        consumer.Subscribe(topicConsumer.TopicName);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(cancellationToken);
                if (result?.Message is null)
                {
                    continue;
                }
                var processed = await CommitAfterHandlingAsync(
                    () => HandleExternalMessageAsync(
                        CreateReceivedMessage(result, ServiceKey), cancellationToken, topicConsumer.WorkloadOwner),
                    () => consumer.Commit(result),
                    () => consumer.Seek(result.TopicPartitionOffset),
                    failure =>
                    {
                        var idBytes = result.Message.Headers?.LastOrDefault(header => header.Key == "monica-message-id")?.GetValueBytes();
                        var messageId = idBytes is null ? null : Encoding.UTF8.GetString(idBytes);
                        TopicStatusStore.ReportError(ServiceKey, topicConsumer.TopicName,
                            $"Retry budget exhausted for Kafka message {messageId} at {result.TopicPartitionOffset}",
                            failure);
                        RecordState($"Kafka message retry budget exhausted for topic {topicConsumer.TopicName}",
                            HostedServiceState.Degraded, failure);
                        Logger.LogError(failure,
                            "Retry budget exhausted for Kafka message {MessageId} at {TopicPartitionOffset}; skipping delivery " +
                            "(ServiceKey: {ServiceKey})",
                            messageId, result.TopicPartitionOffset, ServiceKey ?? "default");
                    },
                    retryPolicy, cancellationToken);

                if (processed)
                {
                    TopicStatusStore.ReportMessageProcessed(ServiceKey, topicConsumer.TopicName);
                }

                if (!hasProvenHealthy)
                {
                    hasProvenHealthy = true;
                    ReportSubscriptionState(
                        group, TopicSubscriptionRuntimeState.Healthy,
                        $"Kafka consumer is delivering messages for topic {topicConsumer.TopicName}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (KafkaConsumerRestartRequiredException)
            {
                // After a failed seek, this consumer cannot safely commit any later offset.
                throw;
            }
            catch (KafkaException ex) when (IsOwnershipLoss(ex))
            {
                throw new KafkaConsumerRestartRequiredException("Kafka consumer lost partition ownership.", ex);
            }
            catch (KafkaException ex) when (ex.Error.IsFatal)
            {
                throw;
            }
            catch (Exception ex)
            {
                hasProvenHealthy = false;
                ReportSubscriptionState(
                    group, TopicSubscriptionRuntimeState.Recovering,
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
        catch (KafkaException ex) when (IsOwnershipLoss(ex))
        {
            // A rebalance may have transferred the partition after processing. Seeking it is
            // invalid; the new owner retries the original record from its committed position.
            throw new KafkaConsumerRestartRequiredException("Kafka partition ownership changed before acknowledgment.", ex);
        }
        catch (Exception failure)
        {
            RewindOrThrow(rewind, failure);
            throw;
        }

        return processed;
    }

    internal static EventMessage CreateReceivedMessage(ConsumeResult<string, string> result, string? serviceKey)
    {
        string? Header(string name)
        {
            var value = result.Message?.Headers?.LastOrDefault(header => header.Key == name)?.GetValueBytes();
            return value is null ? null : Encoding.UTF8.GetString(value);
        }

        string RequiredHeader(string name)
        {
            var value = Header(name);
            return !string.IsNullOrWhiteSpace(value) ? value
                : throw new KafkaNativeEnvelopeException(
                    $"Kafka message on topic '{result.Topic}' lacks required native EventBus header '{name}'. " +
                    "Dapr CloudEvents must be drained before switching providers.");
        }

        var metadata = new EventDeliveryMetadata(
            RequiredHeader("monica-message-id"), RequiredHeader("monica-source"), RequiredHeader("monica-event-name"),
            result.Topic, serviceKey, Header("traceparent"), Header("tracestate"));
        if (result.Message?.Value is null)
        {
            throw new EventMessageDeserializationException($"Kafka message on topic '{result.Topic}' has a null body.");
        }

        metadata = metadata with { TransportKey = result.Message.Key };
        return new EventMessage(metadata, EventSubscriptionScope.Distributed, Encoding.UTF8.GetBytes(result.Message.Value));
    }

    // A protocol mismatch consumes the same finite budget as any other processing failure.
    internal sealed class KafkaNativeEnvelopeException(string message) : Exception(message)
    {
    }

    private static bool IsOwnershipLoss(KafkaException exception)
        => IsOwnershipLoss(exception.Error.Code)
            || exception is TopicPartitionOffsetException partitionFailure
                && partitionFailure.Results.Any(result => IsOwnershipLoss(result.Error.Code));

    private static bool IsOwnershipLoss(ErrorCode code)
        => code is ErrorCode.Local_State or ErrorCode.Local_AssignmentLost or ErrorCode.Local_MaxPollExceeded
            or ErrorCode.IllegalGeneration or ErrorCode.UnknownMemberId or ErrorCode.RebalanceInProgress;

    private static void RewindOrThrow(Action rewind, Exception originalFailure)
    {
        try
        {
            rewind();
        }
        catch (Exception seekFailure)
        {
            throw new KafkaConsumerRestartRequiredException(
                "Kafka failed to rewind an unacknowledged message; the consumer must restart from its committed position.",
                new AggregateException(originalFailure, seekFailure));
        }
    }

    internal sealed class KafkaConsumerRestartRequiredException(string message, Exception innerException)
        : Exception(message, innerException)
    {
    }

    /// <summary>
    /// Owns publication and draining of topic consumers across service lifecycle transitions.
    /// </summary>
    internal sealed class TopicConsumerRegistry
    {
        private readonly object _lifecycleLock = new();
        private readonly Dictionary<(string TopicName, string? WorkloadOwner), TopicConsumer> _consumers = new();
        private bool _isAccepting = true;

        internal bool TryAddAndStart(TopicConsumer consumer)
        {
            lock (_lifecycleLock)
            {
                var key = (consumer.TopicName, consumer.WorkloadOwner);
                if (!_isAccepting || !_consumers.TryAdd(key, consumer))
                {
                    return false;
                }

                if (consumer.TryStart())
                {
                    return true;
                }

                _consumers.Remove(key);
                return false;
            }
        }

        internal TopicConsumer? Remove(string topicName, string? workloadOwner = null)
        {
            lock (_lifecycleLock)
            {
                return _consumers.Remove((topicName, workloadOwner), out var consumer)
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
            CancellationToken cancellationToken,
            Func<TopicConsumer, Task> consumeAsync,
            string? workloadOwner = null)
        {
            TopicName = topicName;
            WorkloadOwner = workloadOwner;
            _consumeAsync = consumeAsync;
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken = _cancellationTokenSource.Token;
        }

        public string TopicName { get; }

        public string? WorkloadOwner { get; }

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
