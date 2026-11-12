using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Abstractions.Communication;
using Monica.DataChannel.Pipeline;
using Monica.DataChannel.Providers.Kafka;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.DataChannel.Providers.Kafka;

public sealed class KafkaProducerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Output_WhenPayloadIsExplicit_ShouldPublishExactBytesAndStableResolvedKey(int format)
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        scenario.OutputKey = "ordering:订单";
        byte[] bytes = [0, 128, 255];
        object? payload = format switch
        {
            0 => "订单\0é",
            1 => bytes,
            2 => bytes.AsMemory(),
            3 => (ReadOnlyMemory<byte>)bytes,
            4 => "",
            _ => null
        };
        byte[]? expected = format switch
        {
            0 => Encoding.UTF8.GetBytes("订单\0é"),
            4 => [],
            5 => null,
            _ => [0, 128, 255]
        };
        await using var application = await StartAsync(scenario, deadline.Token);
        await Channel(application).SendDataFromInnerAsync(payload!, deadline.Token);
        bytes[0] = 99;

        var record = Assert.Single(Assert.Single(scenario.Clients.Producers).Records);
        Assert.Equal(KafkaScenario.TOPIC, record.Topic);
        Assert.Equal(expected, record.Message.Value);
        Assert.Equal(Encoding.UTF8.GetBytes(scenario.OutputKey), record.Message.Key);
        Assert.Equal(1, Volatile.Read(ref scenario.KeyResolutionCalls));
        Assert.Equal(Acks.All, scenario.Clients.ProducerConfiguration!.Acks);
        Assert.True(scenario.Clients.ProducerConfiguration.EnableIdempotence);
    }

    [Fact]
    public async Task Output_WhenMetadataOverridesKeyAndHeaders_ShouldRetainRawNullDuplicateAndEmptyValues()
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        scenario.OutputKey = "should-not-be-used";
        await using var application = await StartAsync(scenario, deadline.Token);
        byte[] key = [0, 255];
        byte[] header = [1, 128];
        var context = new ChannelDataContext(ChannelSide.Inner, "payload") { CancellationToken = deadline.Token };
        var metadata = (IDictionary<string, object?>)context.Metadata;
        metadata[KafkaMessageMetadata.OUTPUT_KEY_METADATA_KEY] = key;
        metadata[KafkaMessageMetadata.OUTPUT_HEADERS_METADATA_KEY] = new KafkaMessageHeader[]
        {
            new("same", null), new("same", []), new("same", header)
        };
        await Channel(application).Pipe.SendDataAsync(context);
        key[0] = 99;
        header[0] = 99;

        var message = Assert.Single(Assert.Single(scenario.Clients.Producers).Records).Message;
        Assert.Equal(new byte[] { 0, 255 }, message.Key);
        Assert.Equal(["same", "same", "same"], message.Headers.Select(item => item.Key).ToArray());
        Assert.Null(message.Headers[0].GetValueBytes());
        Assert.Empty(message.Headers[1].GetValueBytes());
        Assert.Equal(new byte[] { 1, 128 }, message.Headers[2].GetValueBytes());
        Assert.Equal(0, Volatile.Read(ref scenario.KeyResolutionCalls));

        metadata[KafkaMessageMetadata.OUTPUT_KEY_METADATA_KEY] = null;
        await Channel(application).Pipe.SendDataAsync(context);
        Assert.Null(Assert.Single(scenario.Clients.Producers).Records.Last().Message.Key);
        Assert.Equal(0, Volatile.Read(ref scenario.KeyResolutionCalls));
    }

    [Fact]
    public async Task Output_WhenApplicationSuppliesPartitioner_ShouldPassTheExactDelegateAndSerializedKey()
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        PartitionerDelegate mapping = static (_, count, key, isNull) => new Partition(isNull ? 0 : key[0] % count);
        scenario.Options.Partitioner = mapping;
        scenario.OutputKey = "stable";
        await using var application = await StartAsync(scenario, deadline.Token);
        await Channel(application).SendDataFromInnerAsync("payload", deadline.Token);

        Assert.Same(mapping, scenario.Clients.Partitioner);
        var record = Assert.Single(Assert.Single(scenario.Clients.Producers).Records);
        Assert.Equal((byte)'s' % 7, scenario.Clients.Partitioner!(record.Topic, 7, record.Message.Key, false).Value);
    }

    [Theory]
    [InlineData(PersistenceStatus.NotPersisted)]
    [InlineData(PersistenceStatus.PossiblyPersisted)]
    public async Task Output_WhenBrokerDoesNotConfirmPersistence_ShouldFailThePipeline(PersistenceStatus status)
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        scenario.Clients.Produce = (record, _) => Task.FromResult(new DeliveryResult<byte[], byte[]>
        {
            Topic = record.Topic, Partition = 0, Offset = 0, Message = record.Message, Status = status
        });
        await using var application = await StartAsync(scenario, deadline.Token);

        await Assert.ThrowsAsync<IOException>(() => Channel(application).SendDataFromInnerAsync("payload", deadline.Token));

        Assert.True(Channel(application).Pipe.HasExceptions);
        Assert.Single(Assert.Single(scenario.Clients.Producers).Records);
    }

    [Fact]
    public async Task Output_WhenNativeSendFails_ShouldPreserveTheFailureForTheCaller()
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        var failure = new IOException("Scripted broker connection failure.");
        scenario.Clients.Produce = (_, _) => Task.FromException<DeliveryResult<byte[], byte[]>>(failure);
        await using var application = await StartAsync(scenario, deadline.Token);

        var actual = await Assert.ThrowsAsync<IOException>(() => Channel(application).SendDataFromInnerAsync("payload", deadline.Token));

        Assert.Same(failure, actual);
        Assert.Single(Assert.Single(scenario.Clients.Producers).Records);
    }

    [Fact]
    public async Task Output_WhenCallerCancelsPendingDelivery_ShouldPropagateCancellationWithoutRetry()
    {
        using var deadline = Deadline();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var scenario = OutputScenario();
        var entered = Signal();
        scenario.Clients.Produce = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Canceled producer work cannot complete.");
        };
        await using var application = await StartAsync(scenario, deadline.Token);
        var send = Channel(application).SendDataFromInnerAsync("payload", canceled.Token);
        await entered.Task.WaitAsync(deadline.Token);
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.Single(Assert.Single(scenario.Clients.Producers).Records);
    }

    [Fact]
    public async Task Output_WhenPayloadIsAnObject_ShouldRejectImplicitToStringSerialization()
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        await using var application = await StartAsync(scenario, deadline.Token);

        await Assert.ThrowsAsync<ArgumentException>(() => Channel(application).SendDataFromInnerAsync(new { MessageId = "stable-id" }, deadline.Token));

        Assert.Empty(Assert.Single(scenario.Clients.Producers).Records);
    }

    [Fact]
    public async Task Configuration_WhenSaslAndTlsAreSelected_ShouldPreserveSecretsOnlyAtTheNativeBoundary()
    {
        using var deadline = Deadline();
        var scenario = new KafkaScenario();
        var options = scenario.Options;
        options.Direction = ConnectionDirection.InputAndOutput;
        options.SecurityProtocol = SecurityProtocol.SaslSsl;
        options.SaslMechanism = SaslMechanism.ScramSha256;
        options.Username = "exact-user";
        options.Password = "  exact password  ";
        options.SslCaLocation = "test-ca.pem";
        options.ClientId = "diagnostic-client";
        options.ConsumerGroupId = "stable-logical-owner";
        options.ConsumerMaxPollInterval = TimeSpan.FromSeconds(23);
        options.ConsumerSessionTimeout = TimeSpan.FromSeconds(7);
        options.ConsumerFetchMessageMaxBytes = 2 * 1024 * 1024;
        options.ConsumerFetchMaxBytes = 3 * 1024 * 1024;
        options.ConsumerQueuedMaxMessagesKbytes = 8192;
        options.ProducerMessageMaxBytes = 5 * 1024 * 1024;
        options.ProducerDeliveryTimeout = TimeSpan.FromSeconds(17);
        await using var application = await StartAsync(scenario, deadline.Token);
        await scenario.Signal.UntilAsync(() => scenario.Clients.Consumers.Count == 1, deadline.Token);

        var consumer = scenario.Clients.ConsumerConfiguration!;
        Assert.False(consumer.EnableAutoCommit);
        Assert.False(consumer.EnableAutoOffsetStore);
        Assert.Equal(AutoOffsetReset.Error, consumer.AutoOffsetReset);
        Assert.Equal("stable-logical-owner", consumer.GroupId);
        Assert.Equal(23000, consumer.MaxPollIntervalMs);
        Assert.Equal(7000, consumer.SessionTimeoutMs);
        Assert.Equal(2 * 1024 * 1024, consumer.MaxPartitionFetchBytes);
        Assert.Equal(3 * 1024 * 1024, consumer.FetchMaxBytes);
        Assert.Equal(8192, consumer.QueuedMaxMessagesKbytes);
        var producer = scenario.Clients.ProducerConfiguration!;
        Assert.Equal(5 * 1024 * 1024, producer.MessageMaxBytes);
        Assert.Equal(17000, producer.MessageTimeoutMs);
        foreach (var client in new ClientConfig[] { consumer, producer })
        {
            Assert.Equal("diagnostic-client", client.ClientId);
            Assert.Equal(SecurityProtocol.SaslSsl, client.SecurityProtocol);
            Assert.Equal(SaslMechanism.ScramSha256, client.SaslMechanism);
            Assert.Equal("exact-user", client.SaslUsername);
            Assert.Equal("  exact password  ", client.SaslPassword);
            Assert.Equal("test-ca.pem", client.SslCaLocation);
        }
        var evidence = JsonSerializer.Serialize((object)Channel(application).Pipe.OuterEndpoint.GetMetadata());
        Assert.DoesNotContain("exact-user", evidence);
        Assert.DoesNotContain("exact password", evidence);
        Assert.DoesNotContain("Username", evidence);
        Assert.DoesNotContain("Password", evidence);
        Assert.Contains("stable-logical-owner", evidence);
    }

    [Fact]
    public async Task Shutdown_WhenPublicationIsPending_ShouldDrainItBeforeDisposingProducer()
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        var entered = Signal();
        var release = Signal();
        scenario.Clients.Produce = async (record, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new DeliveryResult<byte[], byte[]> { Message = record.Message, Status = PersistenceStatus.Persisted };
        };
        var application = await StartAsync(scenario, deadline.Token);
        try
        {
            var producer = Assert.Single(scenario.Clients.Producers);
            var send = Channel(application).SendDataFromInnerAsync("payload", deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            var stop = application.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, producer.DisposeCalls);
            release.TrySetResult();
            await send.WaitAsync(deadline.Token);
            await stop.WaitAsync(deadline.Token);
            Assert.Equal(1, producer.DisposeCalls);
        }
        finally { release.TrySetResult(); await application.DisposeAsync(); }
    }

    [Fact]
    public async Task Shutdown_WhenNativeFlushLeavesOutstandingMessages_ShouldDisposeProducerAndReportFailure()
    {
        using var deadline = Deadline();
        var scenario = OutputScenario();
        scenario.Clients.Flush = () => 1;
        await using var application = await StartAsync(scenario, deadline.Token);
        var producer = Assert.Single(scenario.Clients.Producers);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => ((KafkaEndpoint)Channel(application).Pipe.OuterEndpoint).DisposeAsync(deadline.Token));

        Assert.IsType<TimeoutException>(Assert.Single(failure.InnerExceptions));
        Assert.Equal(1, producer.DisposeCalls);
    }

    [Fact]
    public async Task Pipeline_WhenTargetCancelsBeforeReturning_ShouldRejectItsApparentSuccess()
    {
        using var deadline = Deadline();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var scenario = OutputScenario();
        scenario.Receive = _ => { canceled.Cancel(); return Task.CompletedTask; };
        await using var application = await StartAsync(scenario, deadline.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Channel(application).SendDataFromOuterAsync("input", canceled.Token));

        Assert.Single(scenario.Deliveries);
    }

    [Fact]
    public void Options_WhenAutoCommitOrUnknownSecurityIsSelected_ShouldRejectUnsafeConfiguration()
    {
        var scenario = OutputScenario();
        scenario.Options.EnableAutoCommit = true;
        Assert.Throws<ArgumentException>(scenario.Options.EnrichOrValidate);
        scenario.Options.EnableAutoCommit = false;
        scenario.Options.SecurityProtocol = (SecurityProtocol)9876;
        Assert.Throws<ArgumentException>(scenario.Options.EnrichOrValidate);
    }

    private static KafkaScenario OutputScenario()
    {
        var scenario = new KafkaScenario();
        scenario.Options.Direction = ConnectionDirection.Output;
        return scenario;
    }
    private static async Task<MonicaTestApplication> StartAsync(KafkaScenario scenario, CancellationToken cancellationToken)
    {
        var application = await new KafkaTestApplicationFactory(scenario).CreateAsync(cancellationToken: cancellationToken);
        await scenario.Signal.UntilAsync(() => scenario.Clients.Producers.Count == 1, cancellationToken);
        return application;
    }
    private static global::Monica.DataChannel.DataChannel Channel(MonicaTestApplication application) =>
        application.Services.GetRequiredService<IDataChannelManager>().Fetch(KafkaScenario.CHANNEL_ID)!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return deadline;
    }
}
