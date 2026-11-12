using Confluent.Kafka;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Services.Support;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Kafka.Services.Support;

public sealed class KafkaClientConfigFactoryTests
{
    [Fact]
    public void BuildProducerConfig_WhenUsingDefaults_ShouldRequireBrokerAcknowledgmentAndBoundRetries()
    {
        var config = KafkaClientConfigFactory.BuildProducerConfig(Cluster(), new ModuleEventBusKafkaOption());

        Assert.Equal(Acks.All, config.Acks);
        Assert.True(config.EnableIdempotence);
        Assert.Equal(1_000_000, config.MessageMaxBytes);
        Assert.Equal(120_000, config.MessageTimeoutMs);
    }

    [Fact]
    public void BuildConsumerConfig_WhenUsingDefaults_ShouldRequireProvisionedOffsetsAndPreserveManualAcknowledgment()
    {
        var config = KafkaClientConfigFactory.BuildConsumerConfig(Cluster(), new ModuleEventBusKafkaOption(), null);

        Assert.Equal(AutoOffsetReset.Error, config.AutoOffsetReset);
        Assert.False(config.EnableAutoCommit);
        Assert.False(config.EnableAutoOffsetStore);
        Assert.Equal(1_048_576, config.MaxPartitionFetchBytes);
        Assert.Equal(50 * 1024 * 1024, config.FetchMaxBytes);
        Assert.Equal(45_000, config.SessionTimeoutMs);
        Assert.Equal(300_000, config.MaxPollIntervalMs);
        Assert.Equal("monica-eventbus.default", config.GroupId);
    }

    [Fact]
    public void BuildClientConfigs_WhenLargeMessagesAndExplicitOffsetsAreConfigured_ShouldTransferRuntimeLimits()
    {
        var option = new ModuleEventBusKafkaOption
        {
            ConsumerGroupId = "fips.site-a",
            ProducerMessageMaxBytes = 100 * 1024 * 1024,
            ProducerDeliveryTimeout = TimeSpan.FromSeconds(90),
            ConsumerFetchMessageMaxBytes = 100 * 1024 * 1024,
            ConsumerFetchMaxBytes = 100 * 1024 * 1024,
            ConsumerSessionTimeout = TimeSpan.FromSeconds(30),
            ConsumerMaxPollInterval = TimeSpan.FromMinutes(10),
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        var producer = KafkaClientConfigFactory.BuildProducerConfig(Cluster(), option);
        var consumer = KafkaClientConfigFactory.BuildConsumerConfig(Cluster(), option, "audit", "user.realtime");

        Assert.Equal(100 * 1024 * 1024, producer.MessageMaxBytes);
        Assert.Equal(90_000, producer.MessageTimeoutMs);
        Assert.Equal(100 * 1024 * 1024, consumer.MaxPartitionFetchBytes);
        Assert.Equal(100 * 1024 * 1024, consumer.FetchMaxBytes);
        Assert.Equal(30_000, consumer.SessionTimeoutMs);
        Assert.Equal(600_000, consumer.MaxPollIntervalMs);
        Assert.Equal(AutoOffsetReset.Earliest, consumer.AutoOffsetReset);
        Assert.Equal("fips.site-a.owner.user%2Erealtime.bus.audit", consumer.GroupId);
    }

    [Fact]
    public void BuildConsumerGroupId_WhenOwnerOrBusContainsDelimiters_ShouldKeepLogicalReceiversDistinct()
    {
        var option = new ModuleEventBusKafkaOption { ConsumerGroupId = "fips.site-a" };
        var groups = new[]
        {
            KafkaClientConfigFactory.BuildConsumerGroupId(option, null),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, null, "default"),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, "b", "a"),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, null, "a.bus.b"),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, "b.c", "a"),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, "c", "a.bus.b"),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, null, "a%2Eb"),
            KafkaClientConfigFactory.BuildConsumerGroupId(option, null, "a.b")
        };

        Assert.Equal(groups.Length, groups.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(groups[2], KafkaClientConfigFactory.BuildConsumerGroupId(option, "b", "a"));
    }

    [Theory]
    [InlineData(" leading and trailing ")]
    [InlineData("   ")]
    public void BuildProducerConfig_WhenSaslPasswordContainsWhitespace_ShouldPreserveExactSecret(string password)
    {
        var cluster = Cluster();
        cluster.SecurityProtocol = "SaslSsl";
        cluster.SaslMechanism = "Plain";
        cluster.SaslUsername = "test-user";
        cluster.SaslPassword = password;

        Assert.Equal(password, cluster.Clone().Normalize().SaslPassword);
        Assert.Equal(password, KafkaClientConfigFactory.BuildProducerConfig(cluster, new ModuleEventBusKafkaOption()).SaslPassword);
    }

    [Theory]
    [InlineData("SecurityProtocol", "999")]
    [InlineData("SecurityProtocol", "unknown")]
    [InlineData("SaslMechanism", "999")]
    [InlineData("SaslMechanism", "unknown")]
    public void BuildProducerConfig_WhenSecurityEnumIsInvalid_ShouldRejectBeforeCreatingNativeClient(
        string property, string value)
    {
        var cluster = Cluster();
        if (property == nameof(KafkaClusterConfig.SecurityProtocol))
            cluster.SecurityProtocol = value;
        else
            cluster.SaslMechanism = value;

        Assert.Throws<InvalidOperationException>(() =>
            KafkaClientConfigFactory.BuildProducerConfig(cluster, new ModuleEventBusKafkaOption()));
    }

    [Fact]
    public void BuildProducerConfig_WhenDeliveryDeadlineIsUnbounded_ShouldRejectBeforeCreatingNativeClient()
    {
        var option = new ModuleEventBusKafkaOption { ProducerDeliveryTimeout = TimeSpan.Zero };

        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaClientConfigFactory.BuildProducerConfig(Cluster(), option));
    }

    [Fact]
    public void BuildConsumerConfig_WhenPollIntervalIsShorterThanSession_ShouldRejectBeforeCreatingNativeClient()
    {
        var option = new ModuleEventBusKafkaOption
        {
            ConsumerSessionTimeout = TimeSpan.FromSeconds(45),
            ConsumerMaxPollInterval = TimeSpan.FromSeconds(30)
        };

        Assert.Throws<ArgumentException>(() => KafkaClientConfigFactory.BuildConsumerConfig(Cluster(), option, null));
    }

    [Fact]
    public void BuildConsumerConfig_WhenOffsetPolicyIsUndefined_ShouldRejectBeforeCreatingNativeClient()
    {
        var option = new ModuleEventBusKafkaOption { AutoOffsetReset = (AutoOffsetReset)999 };

        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaClientConfigFactory.BuildConsumerConfig(Cluster(), option, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildClientConfigs_WhenMessageLimitIsInvalid_ShouldRejectBeforeCreatingNativeClient(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaClientConfigFactory.BuildProducerConfig(Cluster(),
            new ModuleEventBusKafkaOption { ProducerMessageMaxBytes = limit }));
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaClientConfigFactory.BuildConsumerConfig(Cluster(),
            new ModuleEventBusKafkaOption { ConsumerFetchMessageMaxBytes = limit }, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaClientConfigFactory.BuildConsumerConfig(Cluster(),
            new ModuleEventBusKafkaOption { ConsumerFetchMaxBytes = limit }, null));
    }

    private static KafkaClusterConfig Cluster() => new()
    {
        ClusterId = "tests",
        BootstrapServers = "broker.example:9092"
    };
}
