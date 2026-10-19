using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core.Modularity.Abstractions;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Abstractions.Partitioning;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Providers.Default;
using Monica.DataChannel.Providers.Kafka;
using Monica.Modules;
using Monica.Testing.Hosting;

namespace Test.Monica.DataChannel.Providers.Kafka;

internal sealed class KafkaTestApplicationFactory(KafkaScenario scenario)
    : MonicaTestApplicationFactory<KafkaTestApplicationFactory>
{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.ConfigureModuleSystem(options =>
        {
            options.AutoAddMonicaHttpListener = false;
            options.EnableMinimalApiByDefault = false;
        });
        builder.AddDataChannel(options => options.InitThreadCount = 1).UseSetup<KafkaScenarioSetup>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton(scenario);
        services.RemoveAll<IKafkaDataChannelClientFactory>();
        services.AddSingleton<IKafkaDataChannelClientFactory>(scenario.Clients);
        services.AddSingleton<IDataChannelPartitionKeyResolver>(new KafkaScenarioPartitionKeyResolver(scenario));
    }
}

internal sealed class KafkaScenario
{
    internal const string CHANNEL_ID = "kafka-contract";
    internal const string TOPIC = "scenario-topic";
    internal KafkaOptions Options { get; } = new(ConnectionDirection.Input)
    {
        BootstrapServers = "scripted.invalid:9092", Topic = TOPIC, ConsumerGroupId = "scenario-owner",
        SecurityProtocol = SecurityProtocol.Plaintext, RetryDelay = TimeSpan.FromMilliseconds(1)
    };
    internal KafkaStateSignal Signal { get; } = new();
    internal ScriptedKafkaClientFactory Clients { get; }
    internal ConcurrentQueue<ChannelDataContext> Deliveries { get; } = new();
    internal Func<ChannelDataContext, Task> Receive { get; set; } = _ => Task.CompletedTask;
    internal string? OutputKey { get; set; }
    internal int KeyResolutionCalls;

    internal KafkaScenario() => Clients = new ScriptedKafkaClientFactory(Signal);

    internal static KafkaConsumerSnapshot Snapshot(global::Monica.DataChannel.DataChannel channel)
    {
        var metadata = (object)channel.Pipe.OuterEndpoint.GetMetadata();
        return (KafkaConsumerSnapshot)metadata.GetType().GetProperty("Consumer")!.GetValue(metadata)!;
    }

    internal static ConsumeResult<byte[], byte[]> Record(int partition, long offset, string? value = "value") =>
        new()
        {
            Topic = TOPIC, Partition = partition, Offset = offset,
            Message = new Message<byte[], byte[]>
            {
                Key = Encoding.UTF8.GetBytes($"resource-{partition}"),
                Value = value is null ? null! : Encoding.UTF8.GetBytes(value),
                Headers = new Headers(), Timestamp = new Timestamp(new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc))
            }
        };
}

internal sealed class KafkaScenarioSetup(KafkaScenario scenario) : IDataChannelSetup
{
    public void Setup(IDataChannelRegistrar registrar) => registrar.Add(KafkaScenario.CHANNEL_ID,
        pipe => pipe.SetOuterEndpoint(scenario.Options).SetInnerEndpoint<KafkaScenarioReceiver>());
}

internal sealed class KafkaScenarioReceiver(KafkaScenario scenario) : DefaultChannelEndpoint
{
    public override async Task ReceiveDataAsync(ChannelDataContext data)
    {
        scenario.Deliveries.Enqueue(data);
        scenario.Signal.Pulse();
        await scenario.Receive(data);
        scenario.Signal.Pulse();
    }
}

internal sealed class KafkaScenarioPartitionKeyResolver(KafkaScenario scenario) : IDataChannelPartitionKeyResolver
{
    public ValueTask<string?> ResolvePartitionKeyAsync(ChannelDataContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref scenario.KeyResolutionCalls);
        return ValueTask.FromResult(scenario.OutputKey);
    }
}

internal sealed class KafkaStateSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Pulse()
    {
        TaskCompletionSource previous;
        lock (_gate) { previous = _changed; _changed = NewSignal(); }
        previous.TrySetResult();
    }

    internal async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate) changed = _changed.Task;
            if (condition()) return;
            await changed.WaitAsync(cancellationToken);
        }
    }
}

internal sealed class ScriptedKafkaClientFactory(KafkaStateSignal signal) : IKafkaDataChannelClientFactory
{
    internal ConcurrentQueue<ScriptedKafkaConsumer> Consumers { get; } = new();
    internal ConcurrentQueue<ScriptedKafkaProducer> Producers { get; } = new();
    internal ConsumerConfig? ConsumerConfiguration { get; private set; }
    internal ProducerConfig? ProducerConfiguration { get; private set; }
    internal PartitionerDelegate? Partitioner { get; private set; }
    internal Func<KafkaProducedRecord, CancellationToken, Task<DeliveryResult<byte[], byte[]>>>? Produce { get; set; }
    internal Func<int>? Flush { get; set; }

    public IProducer<byte[], byte[]> CreateProducer(ProducerConfig configuration, PartitionerDelegate? partitioner)
    {
        ProducerConfiguration = new ProducerConfig(configuration);
        Partitioner = partitioner;
        var producer = new ScriptedKafkaProducer(signal, () => Produce, () => Flush);
        Producers.Enqueue(producer);
        signal.Pulse();
        return producer.Client;
    }

    public IConsumer<byte[], byte[]> CreateConsumer(ConsumerConfig configuration, KafkaConsumerCallbacks callbacks)
    {
        ConsumerConfiguration = new ConsumerConfig(configuration);
        var consumer = new ScriptedKafkaConsumer(callbacks, signal);
        Consumers.Enqueue(consumer);
        signal.Pulse();
        return consumer.Client;
    }
}

internal sealed record KafkaProducedRecord(string Topic, Message<byte[], byte[]> Message);
internal sealed record KafkaNativeCall(string Method, int ThreadId);

internal sealed class ScriptedKafkaProducer
{
    private readonly KafkaStateSignal _signal;
    private readonly Func<Func<KafkaProducedRecord, CancellationToken, Task<DeliveryResult<byte[], byte[]>>>?> _produce;
    private readonly Func<Func<int>?> _flush;
    private int _disposeCalls;
    internal IProducer<byte[], byte[]> Client { get; }
    internal ConcurrentQueue<KafkaProducedRecord> Records { get; } = new();
    internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

    internal ScriptedKafkaProducer(KafkaStateSignal signal,
        Func<Func<KafkaProducedRecord, CancellationToken, Task<DeliveryResult<byte[], byte[]>>>?> produce,
        Func<Func<int>?> flush)
    {
        _signal = signal; _produce = produce; _flush = flush;
        Client = KafkaNativeClientProxy.Create<IProducer<byte[], byte[]>>(Invoke);
    }

    private object? Invoke(MethodInfo method, object?[] arguments)
    {
        switch (method.Name)
        {
            case "ProduceAsync":
                var topic = arguments[0] is TopicPartition partition ? partition.Topic : (string)arguments[0]!;
                var record = new KafkaProducedRecord(topic, (Message<byte[], byte[]>)arguments[1]!);
                var token = arguments.OfType<CancellationToken>().SingleOrDefault();
                Records.Enqueue(record);
                _signal.Pulse();
                return _produce()?.Invoke(record, token) ?? Task.FromResult(new DeliveryResult<byte[], byte[]>
                {
                    Topic = topic, Partition = 0, Offset = Records.Count - 1, Message = record.Message,
                    Status = PersistenceStatus.Persisted
                });
            case "Flush": return method.ReturnType == typeof(void) ? null : _flush()?.Invoke() ?? 0;
            case "Dispose": Interlocked.Increment(ref _disposeCalls); _signal.Pulse(); return null;
            case "get_Name": return "scripted-producer";
            default: throw new NotSupportedException($"Unexpected producer call: {method.Name}.");
        }
    }
}

internal sealed class ScriptedKafkaConsumer
{
    private readonly KafkaConsumerCallbacks _callbacks;
    private readonly KafkaStateSignal _signal;
    private readonly object _gate = new();
    private readonly List<ConsumeResult<byte[], byte[]>> _records = [];
    private readonly Dictionary<TopicPartitionOffset, ConsumeResult<byte[], byte[]>> _delivered = [];
    private readonly HashSet<TopicPartition> _paused = [];
    private readonly HashSet<TopicPartition> _assigned = [];
    private readonly ConcurrentQueue<Action<KafkaConsumerCallbacks>> _ownership = new();
    private readonly AutoResetEvent _wake = new(false);
    private int _polls;
    private int _disposed;
    internal int CommitFailuresRemaining;
    internal IConsumer<byte[], byte[]> Client { get; }
    internal ConcurrentQueue<KafkaNativeCall> Calls { get; } = new();
    internal ConcurrentQueue<TopicPartitionOffset> CommitAttempts { get; } = new();
    internal ConcurrentQueue<TopicPartitionOffset> Commits { get; } = new();
    internal int Polls => Volatile.Read(ref _polls);
    internal int DisposeCalls => Volatile.Read(ref _disposed);
    internal int PendingRecords { get { lock (_gate) return _records.Count; } }

    internal ScriptedKafkaConsumer(KafkaConsumerCallbacks callbacks, KafkaStateSignal signal)
    {
        _callbacks = callbacks; _signal = signal;
        Client = KafkaNativeClientProxy.Create<IConsumer<byte[], byte[]>>(Invoke);
    }

    internal void Assign(params int[] partitions) => Ownership(partitions, (callbacks, values) => callbacks.Assigned(values), true);
    internal void Revoke(params int[] partitions) => Ownership(partitions, (callbacks, values) => callbacks.Revoked(values), false);
    internal void Lose(params int[] partitions) => Ownership(partitions, (callbacks, values) => callbacks.Lost(values), false);
    private void Ownership(int[] partitions, Action<KafkaConsumerCallbacks, TopicPartition[]> notify, bool assigned)
    {
        var values = partitions.Select(partition => new TopicPartition(KafkaScenario.TOPIC, partition)).ToArray();
        _ownership.Enqueue(callbacks =>
        {
            lock (_gate)
                foreach (var value in values)
                    if (assigned) _assigned.Add(value); else { _assigned.Remove(value); _paused.Remove(value); }
            notify(callbacks, values);
        });
        _wake.Set();
    }

    internal void Enqueue(params ConsumeResult<byte[], byte[]>[] records)
    {
        lock (_gate) _records.AddRange(records);
        _wake.Set();
    }

    internal bool IsPaused(int partition)
    {
        lock (_gate) return _paused.Contains(new TopicPartition(KafkaScenario.TOPIC, partition));
    }

    private object? Invoke(MethodInfo method, object?[] arguments)
    {
        Calls.Enqueue(new KafkaNativeCall(method.Name, Environment.CurrentManagedThreadId));
        switch (method.Name)
        {
            case "Subscribe": return null;
            case "Consume":
                Interlocked.Increment(ref _polls);
                while (_ownership.TryDequeue(out var change)) change(_callbacks);
                ConsumeResult<byte[], byte[]>? result = null;
                lock (_gate)
                {
                    var index = _records.FindIndex(record => _assigned.Contains(record.TopicPartition) && !_paused.Contains(record.TopicPartition));
                    if (index >= 0)
                    {
                        result = _records[index]; _records.RemoveAt(index);
                        _delivered[result.TopicPartitionOffset] = result;
                    }
                }
                _signal.Pulse();
                if (result is null) _wake.WaitOne(TimeSpan.FromMilliseconds(5));
                return result;
            case "Pause":
            case "Resume":
                lock (_gate)
                    foreach (var partition in (IEnumerable<TopicPartition>)arguments[0]!)
                        if (method.Name == "Pause") _paused.Add(partition); else _paused.Remove(partition);
                _signal.Pulse();
                return null;
            case "Commit":
                TopicPartitionOffset[] offsets = arguments[0] is ConsumeResult<byte[], byte[]> received
                    ? [new TopicPartitionOffset(received.TopicPartition, received.Offset + 1)]
                    : ((IEnumerable<TopicPartitionOffset>)arguments[0]!).ToArray();
                foreach (var offset in offsets) CommitAttempts.Enqueue(offset);
                _signal.Pulse();
                if (Interlocked.Decrement(ref CommitFailuresRemaining) >= 0)
                    throw new KafkaException(new Error(ErrorCode.Local_TimedOut, "Scripted commit timeout."));
                foreach (var offset in offsets) Commits.Enqueue(offset);
                _signal.Pulse();
                return method.ReturnType == typeof(void) ? null : offsets.ToList();
            case "get_Assignment": lock (_gate) return _assigned.ToList();
            case "get_Subscription": return new List<string> { KafkaScenario.TOPIC };
            case "get_Name": return "scripted-consumer";
            case "Seek":
                var position = (TopicPartitionOffset)arguments[0]!;
                lock (_gate)
                    if (_delivered.TryGetValue(position, out var rewind) && !_records.Any(record => record.TopicPartitionOffset == position))
                        _records.Insert(0, rewind);
                return null;
            case "Dispose": Interlocked.Increment(ref _disposed); _wake.Dispose(); _signal.Pulse(); return null;
            default: throw new NotSupportedException($"Unexpected consumer call: {method.Name}.");
        }
    }
}

// DispatchProxy keeps the double at Confluent's public client boundary without implementing unused native APIs.
public class KafkaNativeClientProxy : DispatchProxy
{
    private Func<MethodInfo, object?[], object?> _invoke = null!;
    internal static T Create<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var client = Create<T, KafkaNativeClientProxy>();
        ((KafkaNativeClientProxy)(object)client)._invoke = invoke;
        return client;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args ?? []);
}
