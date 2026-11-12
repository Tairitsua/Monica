using System.Text.Json;
using Microsoft.Extensions.Options;
using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Services;
using Monica.Modules;
using NSubstitute;
using Xunit;

namespace Test.Monica.EventBus.Kafka.Services;

public sealed class KafkaClusterServiceTests
{
    [Fact]
    public async Task ListClustersAsync_WhenRuntimeIdentityWasPersisted_ShouldRetainActualRuntimeTargetAndRedactCredentials()
    {
        var repository = new InMemoryKafkaConsoleRepository();
        var runtime = Cluster("runtime", "runtime.example:9092");
        await repository.UpsertClusterAsync(Cluster("runtime", "persisted.example:9092"), TestContext.Current.CancellationToken);
        var options = new ModuleEventBusKafkaOption { DirectEventBusCluster = runtime };
        options.ConfiguredClusters.Add(Cluster("runtime", "configured.example:9092"));
        var service = CreateService(repository, options);

        var summaries = await service.ListClustersAsync(TestContext.Current.CancellationToken);
        var summary = Assert.Single(summaries);
        var privateConfiguration = await service.GetRequiredClusterAsync("runtime", TestContext.Current.CancellationToken);

        Assert.Equal("runtime.example:9092", summary.Config.BootstrapServers);
        Assert.Null(summary.Config.SaslUsername);
        Assert.Null(summary.Config.SaslPassword);
        Assert.DoesNotContain("private-kafka-user", JsonSerializer.Serialize(summaries), StringComparison.Ordinal);
        Assert.DoesNotContain("private-kafka-secret", JsonSerializer.Serialize(summaries), StringComparison.Ordinal);
        Assert.Equal(runtime.SaslUsername, privateConfiguration.SaslUsername);
        Assert.Equal(runtime.SaslPassword, privateConfiguration.SaslPassword);
        Assert.Equal(" private-kafka-secret ", runtime.SaslPassword);
        Assert.Equal(summary.Config.UpdatedAt,
            Assert.Single(await service.ListClustersAsync(TestContext.Current.CancellationToken)).Config.UpdatedAt);
        Assert.Equal("persisted.example:9092", (await repository.GetClusterAsync("runtime", TestContext.Current.CancellationToken))!.BootstrapServers);
    }

    [Fact]
    public async Task MutateCluster_WhenIdentityIsRuntimeOwned_ShouldRejectWithoutChangingPersistence()
    {
        var repository = new InMemoryKafkaConsoleRepository();
        await repository.UpsertClusterAsync(Cluster("runtime", "persisted.example:9092"), TestContext.Current.CancellationToken);
        var service = CreateService(repository, new ModuleEventBusKafkaOption
        {
            DirectEventBusCluster = Cluster("runtime", "runtime.example:9092")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpsertClusterAsync(Cluster("runtime", "replacement.example:9092"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteClusterAsync(" runtime ", TestContext.Current.CancellationToken));

        var persisted = Assert.Single(await repository.GetClustersAsync(TestContext.Current.CancellationToken));
        Assert.Equal("persisted.example:9092", persisted.BootstrapServers);
    }

    [Fact]
    public async Task TestConnectionAsync_WhenSummaryIsRedacted_ShouldUseExactPrivateCredentialsForEveryBrokerOperation()
    {
        var runtime = Cluster("runtime", "runtime.example:9092");
        var admin = Substitute.For<IKafkaAdminProvider>();
        admin.TestConnectionAsync(Arg.Any<KafkaClusterConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new KafkaConnectionTestResult { Brokers = [new KafkaBrokerInfo()] }));
        admin.ListTopicMetadataAsync(Arg.Any<KafkaClusterConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KafkaTopicSummary>>([new(), new()]));
        admin.ListConsumerGroupsAsync(Arg.Any<KafkaClusterConfig>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KafkaConsumerGroupSummary>>([new()]));
        KafkaClusterConfig? probed = null;
        var service = new KafkaClusterService(new InMemoryKafkaConsoleRepository(), admin,
            Options.Create(new ModuleEventBusKafkaOption { DirectEventBusCluster = runtime }),
            (configuration, _) =>
            {
                probed = configuration;
                return Task.FromResult(KafkaBootstrapEndpointProbeResult.Success("runtime.example:9092"));
            });

        var summary = await service.TestConnectionAsync("runtime", TestContext.Current.CancellationToken);

        Assert.True(summary.IsReachable);
        Assert.Equal(1, summary.BrokerCount);
        Assert.Equal(2, summary.TopicCount);
        Assert.Equal(1, summary.ConsumerGroupCount);
        Assert.True(summary.HasTopicInventory);
        Assert.True(summary.HasConsumerGroupInventory);
        Assert.Equal(runtime.SaslUsername, probed!.SaslUsername);
        Assert.Equal(runtime.SaslPassword, probed.SaslPassword);
        await admin.Received(1).TestConnectionAsync(Arg.Is<KafkaClusterConfig>(configuration =>
            configuration.SaslUsername == runtime.SaslUsername && configuration.SaslPassword == runtime.SaslPassword),
            Arg.Any<CancellationToken>());
        await admin.Received(1).ListTopicMetadataAsync(Arg.Is<KafkaClusterConfig>(configuration =>
            configuration.SaslUsername == runtime.SaslUsername && configuration.SaslPassword == runtime.SaslPassword),
            Arg.Any<CancellationToken>());
        await admin.Received(1).ListConsumerGroupsAsync(Arg.Is<KafkaClusterConfig>(configuration =>
            configuration.SaslUsername == runtime.SaslUsername && configuration.SaslPassword == runtime.SaslPassword),
            Arg.Any<CancellationToken>());
        Assert.Null(summary.Config.SaslUsername);
        Assert.Null(summary.Config.SaslPassword);
        Assert.DoesNotContain("private-kafka-secret", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
        Assert.DoesNotContain("private-kafka-user", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpsertClusterAsync_WhenEditingRedactedSummary_ShouldRetainCredentialsUnlessReplaced(bool configured)
    {
        var repository = new InMemoryKafkaConsoleRepository();
        var original = Cluster("console", "console.example:9092");
        var options = new ModuleEventBusKafkaOption();
        if (configured)
            options.ConfiguredClusters.Add(original);
        else
            await repository.UpsertClusterAsync(original, TestContext.Current.CancellationToken);
        var service = CreateService(repository, options);
        var edit = Assert.Single(await service.ListClustersAsync(TestContext.Current.CancellationToken)).Config;
        edit.DisplayName = "Updated display name";

        var summary = await service.UpsertClusterAsync(edit, TestContext.Current.CancellationToken);

        var persisted = (await repository.GetClusterAsync("console", TestContext.Current.CancellationToken))!;
        Assert.Equal("Updated display name", persisted.DisplayName);
        Assert.Equal(original.SaslUsername, persisted.SaslUsername);
        Assert.Equal(original.SaslPassword, persisted.SaslPassword);
        Assert.Null(summary.Config.SaslUsername);
        Assert.Null(summary.Config.SaslPassword);
        edit.SaslUsername = "replacement-user";
        edit.SaslPassword = " replacement-secret ";

        summary = await service.UpsertClusterAsync(edit, TestContext.Current.CancellationToken);

        persisted = (await repository.GetClusterAsync("console", TestContext.Current.CancellationToken))!;
        Assert.Equal("replacement-user", persisted.SaslUsername);
        Assert.Equal(" replacement-secret ", persisted.SaslPassword);
        Assert.DoesNotContain("replacement-secret", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-user", JsonSerializer.Serialize(summary), StringComparison.Ordinal);
    }

    private static KafkaClusterService CreateService(IKafkaConsoleRepository repository, ModuleEventBusKafkaOption options)
        => new(repository, Substitute.For<IKafkaAdminProvider>(), Options.Create(options),
            (_, _) => Task.FromResult(KafkaBootstrapEndpointProbeResult.Success("test")));

    private static KafkaClusterConfig Cluster(string id, string bootstrapServers) => new()
    {
        ClusterId = id,
        DisplayName = id,
        BootstrapServers = bootstrapServers,
        SecurityProtocol = "SaslSsl",
        SaslMechanism = "Plain",
        SaslUsername = "private-kafka-user",
        SaslPassword = " private-kafka-secret "
    };
}
