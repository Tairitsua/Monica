using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Abstractions.Partitioning;
using Monica.DataChannel.Pipeline;

namespace Monica.DataChannel.Providers.Kafka;

/// <summary>Owns native Kafka clients and explicitly acknowledges completed pipeline deliveries.</summary>
/// <remarks>
/// Input has one unacknowledged head per partition and bounded cross-partition workers. Processing failures block
/// their partition after finite retries. Reinitialization drains the old runtime and retries from committed offsets.
/// Partition cancellation is cooperative; application transactions and receipts must reject stale or repeated effects.
/// </remarks>
public class KafkaEndpoint(
    KafkaOptions metadata,
    ILogger<KafkaEndpoint> logger,
    IKafkaDataChannelClientFactory clients,
    IDataChannelPartitionKeyResolver? partitionKeyResolver = null) : CommunicationEndpointBase<KafkaOptions>(metadata)
{
    private static readonly UTF8Encoding UTF8 = new(false, true);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sendGate = new();
    private readonly HashSet<Task> _sends = [];
    private IProducer<byte[], byte[]>? _producer;
    private KafkaConsumerRuntime? _consumer;
    private bool _acceptingSends;

    /// <inheritdoc />
    public override async Task ReceiveDataAsync(ChannelDataContext data)
    {
        Task send;
        lock (_sendGate)
        {
            if (!_acceptingSends || _producer is null)
                throw new InvalidOperationException("The Kafka endpoint is not initialized for output.");
            send = ProduceAsync(_producer, data);
            _sends.Add(send);
        }
        try { await send; }
        finally { lock (_sendGate) _sends.Remove(send); }
    }

    private async Task ProduceAsync(IProducer<byte[], byte[]> producer, ChannelDataContext data)
    {
        var token = data.CancellationToken;
        token.ThrowIfCancellationRequested();
        var entries = (IDictionary<string, object?>)data.Metadata;
        byte[]? key;
        if (entries.TryGetValue(KafkaMessageMetadata.OUTPUT_KEY_METADATA_KEY, out var explicitKey))
            key = Encode(explicitKey, allowNull: true);
        else if (partitionKeyResolver is not null)
        {
            // Application ordering policies inspect the original envelope before payload encoding.
            var resolved = await partitionKeyResolver.ResolvePartitionKeyAsync(data, token);
            key = string.IsNullOrWhiteSpace(resolved) ? null : UTF8.GetBytes(resolved);
        }
        else key = UTF8.GetBytes(data.DataType?.Name ?? "default");
        token.ThrowIfCancellationRequested();
        var headers = new Headers();
        if (entries.TryGetValue(KafkaMessageMetadata.OUTPUT_HEADERS_METADATA_KEY, out var rawHeaders))
        {
            if (rawHeaders is not IEnumerable<KafkaMessageHeader> outputHeaders)
                throw new ArgumentException("Kafka.Headers must contain KafkaMessageHeader values.");
            foreach (var header in outputHeaders)
                headers.Add(header.Key, header.Value?.ToArray());
        }
        else headers.Add("Type", UTF8.GetBytes(data.DataType?.Name ?? "unknown"));
        var message = new Message<byte[], byte[]>
        {
            Key = key!,
            Value = Encode(data.Data, allowNull: true)!,
            Headers = headers
        };
        var delivery = await producer.ProduceAsync(Metadata.Topic!, message, token);
        token.ThrowIfCancellationRequested();
        if (delivery.Status != PersistenceStatus.Persisted)
            throw new IOException("Kafka did not confirm persistence of the output record.");
    }

    private static byte[]? Encode(object? value, bool allowNull) => value switch
    {
        null when allowNull => null,
        string text => UTF8.GetBytes(text),
        byte[] bytes => bytes.ToArray(),
        ReadOnlyMemory<byte> bytes => bytes.ToArray(),
        Memory<byte> bytes => bytes.ToArray(),
        _ => throw new ArgumentException("Kafka payloads and keys must be explicit UTF-8 strings or bytes.")
    };

    /// <inheritdoc />
    public override async Task InitAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            cancellationToken.ThrowIfCancellationRequested();
            Metadata.EnrichOrValidate();
            try
            {
                if (Metadata.Direction is ConnectionDirection.Output or ConnectionDirection.InputAndOutput)
                {
                    _producer = clients.CreateProducer(KafkaClientConfiguration.Producer(Metadata), Metadata.Partitioner);
                    lock (_sendGate) _acceptingSends = true;
                }
                if (Metadata.Direction is ConnectionDirection.Input or ConnectionDirection.InputAndOutput)
                {
                    _consumer = new KafkaConsumerRuntime(Metadata, clients, DeliverAsync,
                        ReportFailure, unavailable => Pipe.IsNotAvailable = unavailable);
                    await _consumer.StartAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                try { await StopCoreAsync(); }
                catch (Exception cleanupFailure) { ReportFailure(cleanupFailure, "Kafka initialization rollback failed."); }
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task DeliverAsync(ConsumeResult<byte[], byte[]> record, CancellationToken cancellationToken)
    {
        if (record.Message is null) throw new InvalidDataException("Kafka supplied a record without a message.");
        var value = record.Message.Value;
        object? payload = value is null ? null : Metadata.InputPayloadFormat == KafkaInputPayloadFormat.RawBytes
            ? value.ToArray() : UTF8.GetString(value);
        var context = CreateData(payload);
        context.CancellationToken = cancellationToken;
        ((IDictionary<string, object?>)context.Metadata)[KafkaMessageMetadata.METADATA_KEY] = new KafkaMessageMetadata(
            record.Topic, record.Partition.Value, record.Offset.Value, record.Message.Key?.ToArray(),
            Array.AsReadOnly(record.Message.Headers?.Select(header => new KafkaMessageHeader(header.Key, header.GetValueBytes()?.ToArray())).ToArray() ?? []),
            record.Message.Timestamp);
        await SendDataAsync(context);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public override async Task DisposeAsync(CancellationToken cancellationToken = default)
    {
        // Teardown must finish even if the caller stops waiting; releasing inner endpoints early abandons live work.
        await _lifecycle.WaitAsync(CancellationToken.None);
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Task[] sends;
        lock (_sendGate)
        {
            _acceptingSends = false;
            sends = _sends.ToArray();
        }
        List<Exception>? failures = null;
        var consumer = _consumer;
        _consumer = null;
        if (consumer is not null)
        {
            try { await consumer.StopAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        // Already-faulted publication tasks belong to their callers. Await all operations before releasing the producer.
        try { await Task.WhenAll(sends); }
        catch { }
        var producer = _producer;
        _producer = null;
        if (producer is not null)
        {
            try
            {
                if (producer.Flush(Metadata.ProducerShutdownTimeout) != 0)
                    throw new TimeoutException("Kafka producer still has outstanding deliveries after its shutdown deadline.");
            }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            finally
            {
                try { producer.Dispose(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }
        if (failures is not null) throw new AggregateException("Kafka endpoint cleanup failed.", failures);
    }

    private void ReportFailure(Exception exception, string message) => CollectException(exception, this, message, logger);

    /// <summary>Returns safe configuration and detached input state, including paused and blocked counts.</summary>
    public override dynamic GetMetadata() => new { Options = Metadata, Consumer = _consumer?.Snapshot };

    /// <inheritdoc />
    public override ConnectionDirection SupportedConnectionDirection() => ConnectionDirection.InputAndOutput;
}
