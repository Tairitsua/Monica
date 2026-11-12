using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Facades;
using Monica.EventBus.Kafka.Localization;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Services;
using Monica.EventBus.Kafka.UIEventBusKafka.State;
using Monica.Modules;
using Xunit;

namespace Test.Monica.EventBus.Kafka.UIEventBusKafka.State;

/// <summary>
/// Exercises configuration-only state transitions through real facade/services. These scenarios
/// deliberately cannot reach Kafka; live successful probes are verified by the real-broker browser fixture.
/// </summary>
public sealed class EventBusKafkaPageStateTests
{
    [Fact]
    public async Task Initialize_WhenRequestedClusterIsMissing_ShouldKeepExactIdentityWithoutDefaultFallback()
    {
        const string requested = "missing:flight/上海?region=APAC&revision=2026#inspection%25";
        await using var fixture = new StateFixture(new InMemoryKafkaConsoleRepository(), External("configured"));

        await fixture.State.InitializeAsync(requested, TestContext.Current.CancellationToken);

        fixture.State.SelectedClusterId.Should().Be(requested);
        fixture.State.RequestedClusterId.Should().Be(requested);
        fixture.State.IsSelectedClusterUnavailable.Should().BeTrue();
        fixture.State.SelectedCluster.Should().BeNull();
        fixture.State.CanAdminSelectedCluster.Should().BeFalse();
        fixture.State.HasDashboardSnapshot.Should().BeFalse();
        fixture.State.HasLoadedTopics.Should().BeFalse();
        fixture.State.HasLoadedConsumerGroups.Should().BeFalse();
        fixture.State.LatestPerformance.Should().BeNull();
    }

    [Fact]
    public async Task Initialize_WhenRequestedCredentialsAreExternal_ShouldKeepConfigurationWithoutInventingBrokerEvidence()
    {
        const string requested = "external:flight/上海?region=APAC#v2%25";
        await using var fixture = new StateFixture(new InMemoryKafkaConsoleRepository(), External(requested));

        await fixture.State.InitializeAsync(requested, TestContext.Current.CancellationToken);

        fixture.State.SelectedClusterId.Should().Be(requested);
        fixture.State.IsSelectedClusterUnavailable.Should().BeFalse();
        fixture.State.SelectedCluster!.Config.CredentialsManagedExternally.Should().BeTrue();
        fixture.State.CanAdminSelectedCluster.Should().BeFalse();
        fixture.State.HasDashboardSnapshot.Should().BeFalse();
        fixture.State.HasLoadedTopics.Should().BeFalse();
        fixture.State.HasLoadedConsumerGroups.Should().BeFalse();
        fixture.State.LatestPerformance.Should().BeNull();
        fixture.State.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Initialize_WhenNoIdentityWasRequested_ShouldRetainExplicitConnectionTestDefault()
    {
        var untested = new KafkaClusterConfig
        {
            ClusterId = "primary", DisplayName = "Primary", BootstrapServers = "invalid:not-a-port"
        };
        await using var fixture = new StateFixture(new InMemoryKafkaConsoleRepository(), untested);

        await fixture.State.InitializeAsync(TestContext.Current.CancellationToken);

        fixture.State.SelectedClusterId.Should().Be("primary");
        fixture.State.RequestedClusterId.Should().BeNull();
        fixture.State.SelectedCluster!.ErrorMessage.Should().BeNull("configuration listing must not manufacture a probe");
        fixture.State.CanAdminSelectedCluster.Should().BeFalse();
        fixture.State.HasDashboardSnapshot.Should().BeFalse();
        fixture.State.HasLoadedTopics.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_WhenExactDirectProbeFailsValidation_ShouldReportFailureWithoutAnEmptyInventory()
    {
        var untested = new KafkaClusterConfig
        {
            ClusterId = "primary", DisplayName = "Primary", BootstrapServers = "invalid:not-a-port"
        };
        await using var fixture = new StateFixture(new InMemoryKafkaConsoleRepository(), untested);

        await fixture.State.InitializeAsync("primary", TestContext.Current.CancellationToken);

        fixture.State.SelectedClusterId.Should().Be("primary");
        fixture.State.SelectedCluster!.IsReachable.Should().BeFalse();
        fixture.State.SelectedCluster.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        fixture.State.ErrorMessage.Should().Be(fixture.State.SelectedCluster.ErrorMessage);
        fixture.State.HasLoadedTopics.Should().BeFalse();
        fixture.State.HasLoadedConsumerGroups.Should().BeFalse();
        fixture.State.HasDashboardSnapshot.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertClusterAsync_WhenOnlyRedactedCredentialsChange_ShouldInvalidatePriorSuccessfulProbe()
    {
        var token = TestContext.Current.CancellationToken;
        var repository = new InMemoryKafkaConsoleRepository();
        var cluster = new KafkaClusterConfig
        {
            ClusterId = "primary", DisplayName = "Primary", BootstrapServers = "broker.invalid:9092",
            SecurityProtocol = "SaslSsl", SaslMechanism = "Plain",
            SaslUsername = "original-user", SaslPassword = "original-secret", UpdatedAt = DateTimeOffset.MinValue
        };
        await using var fixture = new StateFixture(repository, cluster);
        await fixture.State.InitializeAsync(token);
        var previous = fixture.State.SelectedCluster!;
        previous.IsReachable = true;
        previous.HasTopicInventory = true;
        previous.HasConsumerGroupInventory = true;
        var edit = previous.Config.Clone();
        edit.SaslPassword = "replacement-secret";

        var saved = await fixture.State.UpsertClusterAsync(edit, token);

        saved.Should().BeTrue();
        fixture.State.SelectedCluster!.IsReachable.Should().BeFalse();
        fixture.State.SelectedCluster.HasTopicInventory.Should().BeFalse();
        fixture.State.SelectedCluster.HasConsumerGroupInventory.Should().BeFalse();
        fixture.State.SelectedCluster.Config.UpdatedAt.Should().NotBe(previous.Config.UpdatedAt);
        fixture.State.SelectedCluster.Config.SaslUsername.Should().BeNull();
        fixture.State.SelectedCluster.Config.SaslPassword.Should().BeNull();
        fixture.State.CanAdminSelectedCluster.Should().BeFalse();
        (await repository.GetClusterAsync("primary", token))!.SaslPassword.Should().Be("replacement-secret");
    }

    [Fact]
    public async Task Initialize_WhenConfigurationReadIgnoresCancellation_ShouldDrainWithoutPublishingItsLateEvidence()
    {
        var repository = new DeferredRepository();
        await using var fixture = new StateFixture(repository, External("configured"));
        var initialIntegration = fixture.State.Integration;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var load = fixture.State.InitializeAsync("configured", lifetime.Token);
        await repository.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await lifetime.CancelAsync();
        load.IsCompleted.Should().BeFalse("the actual repository read still needs drainage");

        repository.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);

        fixture.State.Integration.Should().BeSameAs(initialIntegration);
        fixture.State.HasLoadedConfiguration.Should().BeFalse();
        fixture.State.Clusters.Should().BeEmpty();
        fixture.State.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Capture_WhenOnlyJmxConfigurationExists_ShouldNotClaimCollectedJmxOrVerifiedMetadata()
    {
        var cluster = External("primary");
        cluster.JmxEndpoint = "http://configuration-only.invalid/jmx";
        await using var fixture = new StateFixture(new InMemoryKafkaConsoleRepository(), cluster);

        var snapshot = await fixture.Performance.CaptureAsync("primary", TestContext.Current.CancellationToken);

        snapshot.IncludesJmxMetrics.Should().BeFalse();
        snapshot.HasMetadataSnapshot.Should().BeFalse();
        snapshot.Message.Should().NotBeNullOrWhiteSpace();
        snapshot.TotalRetainedMessageCount.Should().BeNull();
        snapshot.MessageWriteRatePerSecond.Should().BeNull();
        snapshot.MessageConsumeRatePerSecond.Should().BeNull();
    }

    private static KafkaClusterConfig External(string id) => new()
    {
        ClusterId = id, DisplayName = id, BootstrapServers = "configuration-only.invalid:9092",
        CredentialsManagedExternally = true
    };

    private sealed class StateFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly KafkaPerformancePollingState _polling;

        public StateFixture(IKafkaConsoleRepository repository, params KafkaClusterConfig[] clusters)
        {
            var kafkaOptions = new ModuleEventBusKafkaOption();
            kafkaOptions.ConfiguredClusters.AddRange(clusters);
            var options = Options.Create(kafkaOptions);
            var clusterService = new KafkaClusterService(repository, null!, new KafkaBootstrapEndpointProbe(options), options);
            var integration = new KafkaIntegrationService(_services, clusterService, options);
            Performance = new KafkaPerformanceService(clusterService, null!, null!, null!, repository);
            var dashboard = new KafkaDashboardService(integration, clusterService, Performance);
            var facade = new KafkaConsoleFacade(integration, clusterService, dashboard, null!, null!, Performance);
            _polling = new KafkaPerformancePollingState(facade, Options.Create(new ModuleEventBusKafkaUIOption
            {
                EnablePerformanceAutoRefresh = false
            }));
            State = new EventBusKafkaPageState(facade, _polling, new KeyLocalizer());
        }

        public EventBusKafkaPageState State { get; }
        public KafkaPerformanceService Performance { get; }

        public async ValueTask DisposeAsync()
        {
            await _polling.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    private sealed class KeyLocalizer : IStringLocalizer<EventBusKafkaResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class DeferredRepository : IKafkaConsoleRepository
    {
        private readonly InMemoryKafkaConsoleRepository _inner = new();
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<KafkaClusterConfig>> GetClustersAsync(CancellationToken cancellationToken = default)
        {
            Admitted.TrySetResult();
            await Release.Task;
            return [];
        }

        public Task<KafkaClusterConfig?> GetClusterAsync(string clusterId, CancellationToken cancellationToken = default) =>
            _inner.GetClusterAsync(clusterId, cancellationToken);
        public Task UpsertClusterAsync(KafkaClusterConfig cluster, CancellationToken cancellationToken = default) =>
            _inner.UpsertClusterAsync(cluster, cancellationToken);
        public Task DeleteClusterAsync(string clusterId, CancellationToken cancellationToken = default) =>
            _inner.DeleteClusterAsync(clusterId, cancellationToken);
        public Task ReplacePerformanceSnapshotAsync(KafkaPerformanceSnapshot snapshot, CancellationToken cancellationToken = default) =>
            _inner.ReplacePerformanceSnapshotAsync(snapshot, cancellationToken);
        public Task<KafkaPerformanceSnapshot?> GetLatestPerformanceSnapshotAsync(string clusterId, CancellationToken cancellationToken = default) =>
            _inner.GetLatestPerformanceSnapshotAsync(clusterId, cancellationToken);
    }
}
