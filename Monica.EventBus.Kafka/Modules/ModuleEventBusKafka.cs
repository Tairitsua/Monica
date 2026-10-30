using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Kafka.Abstractions;
using Monica.EventBus.Kafka.Facades;
using Monica.EventBus.Kafka.Localization;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Providers.ConfluentKafka;
using Monica.EventBus.Kafka.Providers.EfCore;
using Monica.EventBus.Kafka.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Kafka EventBus module.
/// </summary>
public static class ModuleEventBusKafkaBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the Kafka EventBus provider and Kafka management console services.
        /// </summary>
        /// <param name="action">Optional module option configuration.</param>
        /// <returns>The host-bound Kafka EventBus registration.</returns>
        public ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> AddEventBusKafka(Action<ModuleEventBusKafkaOption>? action = null)
        {
            var module = builder.AddModule<ModuleEventBusKafka, ModuleEventBusKafkaOption>(action);
            module.Require<ModuleLocalization, ModuleLocalizationOption>()
                .AddResource<EventBusKafkaResource>();
            return module;
        }
    }
}

/// <summary>
/// Kafka EventBus provider and management console backend module.
/// </summary>
public sealed class ModuleEventBusKafka : MonicaModule<ModuleEventBusKafkaOption>, IWebModule,
      IEventBusProviderModule
{
    /// <inheritdoc />
    public Type ProvidesFor => typeof(ModuleEventBus);

    /// <inheritdoc />
    public EventBusProviderKind ProviderType => EventBusProviderKind.Kafka;

    /// <inheritdoc />
    public EventBusProviderCapabilities Capabilities =>
        EventBusProviderCapabilities.BulkPublish |
        EventBusProviderCapabilities.Streaming;

    /// <inheritdoc />
    public string DisplayName => "Kafka";

    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleEventBus, ModuleEventBusOption>();
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleEventBusKafkaOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<IKafkaConsoleRepository, InMemoryKafkaConsoleRepository>();
        services.TryAddSingleton<IKafkaClusterConfigProvider, KafkaClusterConfigProvider>();
        services.TryAddSingleton<KafkaBootstrapEndpointProbe>();
        services.TryAddScoped<IKafkaAdminProvider, ConfluentKafkaAdminProvider>();
        services.TryAddScoped<IKafkaMessageReader, ConfluentKafkaMessageReader>();
        services.TryAddScoped<IKafkaOffsetMetricsProvider, ConfluentKafkaOffsetMetricsProvider>();
        services.TryAddScoped<KafkaIntegrationService>();
        services.TryAddScoped<KafkaClusterService>();
        services.TryAddScoped<KafkaTopicService>();
        services.TryAddScoped<KafkaConsumerGroupService>();
        services.TryAddScoped<KafkaConsumerMetricsService>();
        services.TryAddScoped<KafkaPerformanceService>();
        services.TryAddScoped<KafkaDashboardService>();
        services.TryAddScoped<KafkaConsoleFacade>();
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleEventBusKafkaOption> context)
    {
        var app = context.ApplicationBuilder;
        var localizer = app.ApplicationServices.GetRequiredService<IStringLocalizer<EventBusKafkaResource>>();

        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/eventbus-kafka/integration",
                    async ([FromServices] KafkaConsoleFacade facade, CancellationToken cancellationToken) =>
                        (await facade.GetIntegrationAsync(cancellationToken)).GetResponse())
                .WithName("GetEventBusKafkaIntegration")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Integration:Get:Summary"].Value)
                .WithDescription(localizer["Api:Integration:Get:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters",
                    async ([FromServices] KafkaConsoleFacade facade, CancellationToken cancellationToken) =>
                        (await facade.ListClustersAsync(cancellationToken)).GetResponse())
                .WithName("ListEventBusKafkaClusters")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Clusters:List:Summary"].Value)
                .WithDescription(localizer["Api:Clusters:List:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/clusters",
                    async ([FromBody] KafkaClusterUpsertRequest request,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.UpsertClusterAsync(request, cancellationToken)).GetResponse())
                .WithName("UpsertEventBusKafkaCluster")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Clusters:Upsert:Summary"].Value)
                .WithDescription(localizer["Api:Clusters:Upsert:Description"].Value);

            endpoints.MapDelete("/eventbus-kafka/clusters/{clusterId}",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.DeleteClusterAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("DeleteEventBusKafkaCluster")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Clusters:Delete:Summary"].Value)
                .WithDescription(localizer["Api:Clusters:Delete:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/clusters/{clusterId}/test",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.TestClusterAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("TestEventBusKafkaCluster")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Clusters:Test:Summary"].Value)
                .WithDescription(localizer["Api:Clusters:Test:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/dashboard",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetDashboardAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("GetEventBusKafkaDashboard")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Dashboard:Get:Summary"].Value)
                .WithDescription(localizer["Api:Dashboard:Get:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/topics",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ListTopicsAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("ListEventBusKafkaTopics")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:List:Summary"].Value)
                .WithDescription(localizer["Api:Topics:List:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/topics",
                    async ([FromBody] KafkaTopicCreateRequest request,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.CreateTopicAsync(request, cancellationToken)).GetResponse())
                .WithName("CreateEventBusKafkaTopic")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:Create:Summary"].Value)
                .WithDescription(localizer["Api:Topics:Create:Description"].Value);

            endpoints.MapDelete("/eventbus-kafka/clusters/{clusterId}/topics/{topicName}",
                    async ([FromRoute] string clusterId,
                        [FromRoute] string topicName,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.DeleteTopicAsync(clusterId, topicName, cancellationToken)).GetResponse())
                .WithName("DeleteEventBusKafkaTopic")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:Delete:Summary"].Value)
                .WithDescription(localizer["Api:Topics:Delete:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/clusters/{clusterId}/topics/{topicName}/clear-messages",
                    async ([FromRoute] string clusterId,
                        [FromRoute] string topicName,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ClearTopicMessagesAsync(clusterId, topicName, cancellationToken)).GetResponse())
                .WithName("ClearEventBusKafkaTopicMessages")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:ClearMessages:Summary"].Value)
                .WithDescription(localizer["Api:Topics:ClearMessages:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/topics/partitions",
                    async ([FromBody] KafkaTopicPartitionRequest request,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.IncreasePartitionsAsync(request, cancellationToken)).GetResponse())
                .WithName("IncreaseEventBusKafkaTopicPartitions")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:Partitions:Summary"].Value)
                .WithDescription(localizer["Api:Topics:Partitions:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/topics/retention",
                    async ([FromBody] KafkaTopicRetentionRequest request,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.UpdateRetentionAsync(request, cancellationToken)).GetResponse())
                .WithName("UpdateEventBusKafkaTopicRetention")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:Retention:Summary"].Value)
                .WithDescription(localizer["Api:Topics:Retention:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/topics/messages",
                    async ([FromBody] KafkaTopicMessagesRequest request,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ReadTopicMessagesAsync(request, cancellationToken)).GetResponse())
                .WithName("ReadEventBusKafkaTopicMessages")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:Messages:Summary"].Value)
                .WithDescription(localizer["Api:Topics:Messages:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/topics/{topicName}/backlog",
                    async ([FromRoute] string clusterId,
                        [FromRoute] string topicName,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetTopicBacklogAsync(clusterId, topicName, cancellationToken)).GetResponse())
                .WithName("GetEventBusKafkaTopicBacklog")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Topics:Backlog:Summary"].Value)
                .WithDescription(localizer["Api:Topics:Backlog:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/consumer-groups",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ListConsumerGroupsAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("ListEventBusKafkaConsumerGroups")
                .WithTags(tagName)
                .WithSummary(localizer["Api:ConsumerGroups:List:Summary"].Value)
                .WithDescription(localizer["Api:ConsumerGroups:List:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/topics/{topicName}/consumer-groups",
                    async ([FromRoute] string clusterId,
                        [FromRoute] string topicName,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ListTopicConsumerGroupsAsync(clusterId, topicName, cancellationToken)).GetResponse())
                .WithName("ListEventBusKafkaTopicConsumerGroups")
                .WithTags(tagName)
                .WithSummary(localizer["Api:ConsumerGroups:TopicList:Summary"].Value)
                .WithDescription(localizer["Api:ConsumerGroups:TopicList:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/consumer-groups/{groupId}/members",
                    async ([FromRoute] string clusterId,
                        [FromRoute] string groupId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetConsumerGroupMembersAsync(clusterId, groupId, cancellationToken)).GetResponse())
                .WithName("GetEventBusKafkaConsumerGroupMembers")
                .WithTags(tagName)
                .WithSummary(localizer["Api:ConsumerGroups:Members:Summary"].Value)
                .WithDescription(localizer["Api:ConsumerGroups:Members:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/clusters/{clusterId}/consumer-groups/{groupId}/metrics/capture",
                    async ([FromRoute] string clusterId,
                        [FromRoute] string groupId,
                        [FromQuery] string? topicName,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.CaptureConsumerMetricsAsync(clusterId, topicName, groupId, cancellationToken)).GetResponse())
                .WithName("CaptureEventBusKafkaConsumerGroupMetrics")
                .WithTags(tagName)
                .WithSummary(localizer["Api:ConsumerGroups:Metrics:Summary"].Value)
                .WithDescription(localizer["Api:ConsumerGroups:Metrics:Description"].Value);

            endpoints.MapGet("/eventbus-kafka/clusters/{clusterId}/performance",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetLatestPerformanceAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("GetEventBusKafkaLatestPerformance")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Performance:Latest:Summary"].Value)
                .WithDescription(localizer["Api:Performance:Latest:Description"].Value);

            endpoints.MapPost("/eventbus-kafka/clusters/{clusterId}/performance/capture",
                    async ([FromRoute] string clusterId,
                        [FromServices] KafkaConsoleFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.CapturePerformanceAsync(clusterId, cancellationToken)).GetResponse())
                .WithName("CaptureEventBusKafkaPerformance")
                .WithTags(tagName)
                .WithSummary(localizer["Api:Performance:Capture:Summary"].Value)
                .WithDescription(localizer["Api:Performance:Capture:Description"].Value);
        });
    }
}

/// <summary>
/// Registration extensions for configuring Kafka EventBus and console storage.
/// </summary>
public static class ModuleEventBusKafkaRegistrationExtensions
{
    /// <summary>
    /// Uses the in-memory Kafka console repository.
    /// </summary>
    /// <remarks>
    /// This is the default store. Calling this method explicitly replaces any previous console
    /// repository registration for hosts that want transient runtime configuration.
    /// </remarks>
    /// <param name="module">The Kafka EventBus registration to configure.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> UseInMemoryStore(this ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> module)
    {
        module.ConfigureServices(context =>
        {
            context.Services.RemoveAll<IKafkaConsoleRepository>();
            context.Services.AddSingleton<IKafkaConsoleRepository, InMemoryKafkaConsoleRepository>();
        }, ModuleRegistrationOrder.Late);
        return module;
    }

    /// <summary>
    /// Uses an EF Core repository for Kafka console clusters and performance snapshots.
    /// </summary>
    /// <param name="module">The Kafka EventBus registration to configure.</param>
    /// <param name="optionsAction">Configures the EF Core provider and options for the Kafka console DbContext.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> UseEfCoreStore(this ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> module, Action<IServiceProvider, DbContextOptionsBuilder> optionsAction)
    {
        ArgumentNullException.ThrowIfNull(optionsAction);

        module.Require<ModuleRepository, ModuleRepositoryOption>()
            .AddRepositoryDbContext<KafkaConsoleDbContext>(optionsAction, DbContextProviderType.Default);

        module.ConfigureServices(context =>
        {
            context.Services.RemoveAll<IKafkaConsoleRepository>();
            context.Services.AddScoped<IKafkaConsoleRepository, EfCoreKafkaConsoleRepository>();
        }, ModuleRegistrationOrder.Late);
        return module;
    }

    /// <summary>
    /// Declares a visible Kafka cluster without changing the active EventBus provider.
    /// </summary>
    /// <param name="module">The Kafka EventBus registration to configure.</param>
    /// <param name="cluster">Cluster configuration.</param>
    /// <returns>The current registration.</returns>
    public static ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> AddConfiguredCluster(this ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> module, KafkaClusterConfig cluster)
    {
        var normalized = cluster.Clone().Normalize();
        module.Configure(options => options.AddOrReplaceConfiguredCluster(normalized));
        return module;
    }

    /// <summary>
    /// Enables the native Kafka provider as the default distributed EventBus provider.
    /// </summary>
    /// <param name="module">The Kafka EventBus registration to configure.</param>
    /// <param name="cluster">Cluster used by the native Kafka producer and subscription consumers.</param>
    /// <returns>The current registration.</returns>
    /// <remarks>
    /// This method intentionally performs the provider switch. Registering this module or the UI
    /// alone does not replace an existing Dapr EventBus provider.
    /// </remarks>
    public static ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> UseKafkaProvider(this ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> module, KafkaClusterConfig cluster)
    {
        var normalized = cluster.Clone().Normalize();
        normalized.CredentialsManagedExternally = false;

        module.Require<ModuleEventBus, ModuleEventBusOption>()
            .UseDistributedEventBus<KafkaEventBusProvider>();
        module.Require<ModuleHostedService, ModuleHostedServiceOption>();

        module.Configure(options =>
        {
            options.DirectEventBusCluster = normalized;
            options.PrimaryClusterId = normalized.ClusterId;
            options.AddOrReplaceConfiguredCluster(normalized);
        });

        module.ConfigureServices(context =>
        {
            context.Services.AddHostedService<KafkaEventBusSubscriptionHostedService>();
        }, ModuleRegistrationOrder.Late);

        return module;
    }

    /// <summary>
    /// Declares that the active Dapr EventBus pub/sub component is backed by Kafka.
    /// </summary>
    /// <param name="module">The Kafka EventBus registration to configure.</param>
    /// <param name="cluster">Kafka cluster metadata to show in the console.</param>
    /// <param name="pubSubName">Dapr pub/sub component name. When omitted, the cluster value or module default is used.</param>
    /// <param name="credentialsManagedExternally">
    /// Whether Kafka credentials are owned by Dapr or external infrastructure instead of Monica.
    /// </param>
    /// <returns>The current registration.</returns>
    /// <remarks>
    /// This method is visibility-only. It does not call <c>UseDistributedEventBus</c> and therefore
    /// does not alter an existing Dapr EventBus provider.
    /// </remarks>
    public static ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> UseDaprKafkaIntegration(this ModuleRegistration<ModuleEventBusKafka, ModuleEventBusKafkaOption> module,
        KafkaClusterConfig cluster,
        string? pubSubName = null,
        bool credentialsManagedExternally = true)
    {
        var normalized = cluster.Clone().Normalize();
        normalized.IsDaprBacked = true;
        normalized.CredentialsManagedExternally = credentialsManagedExternally;
        normalized.DaprPubSubName = string.IsNullOrWhiteSpace(pubSubName)
            ? normalized.DaprPubSubName
            : pubSubName.Trim();

        module.Configure(options =>
        {
            options.PrimaryClusterId = normalized.ClusterId;
            options.DaprPubSubName = normalized.DaprPubSubName;
            options.AddOrReplaceConfiguredCluster(normalized);
        });

        return module;
    }

}

/// <summary>
/// Configuration options for the Kafka EventBus provider and console backend.
/// </summary>
public sealed class ModuleEventBusKafkaOption : MinimalApiModuleOptions<ModuleEventBusKafka>
{
    /// <summary>
    /// Gets the clusters declared through module configuration.
    /// </summary>
    /// <remarks>
    /// These clusters are visible in the console even when they are not persisted through the
    /// runtime cluster management page.
    /// </remarks>
    public List<KafkaClusterConfig> ConfiguredClusters { get; } = [];

    /// <summary>
    /// Gets or sets the cluster used by the native Kafka EventBus provider.
    /// </summary>
    /// <remarks>
    /// This value is set by the Kafka provider registration extension.
    /// When it is <see langword="null"/>, registering the module will not replace the default
    /// distributed EventBus provider.
    /// </remarks>
    public KafkaClusterConfig? DirectEventBusCluster { get; set; }

    /// <summary>
    /// Gets or sets the primary cluster selected by integration and dashboard views.
    /// </summary>
    public string? PrimaryClusterId { get; set; }

    /// <summary>
    /// Gets or sets the Dapr pub/sub component name when Kafka backs the Dapr EventBus provider.
    /// </summary>
    public string? DaprPubSubName { get; set; } = "pubsub";

    /// <summary>
    /// Gets or sets the default Kafka client id used when a cluster does not specify one.
    /// </summary>
    public string ClientId { get; set; } = "monica-eventbus-kafka";

    /// <summary>
    /// Gets or sets the Kafka consumer group id used by native EventBus subscriptions.
    /// </summary>
    public string ConsumerGroupId { get; set; } = "monica-eventbus";

    /// <summary>
    /// Gets or sets the request timeout used by Kafka admin operations.
    /// </summary>
    /// <remarks>
    /// Values below one second are normalized to one second before they reach librdkafka so a
    /// non-positive duration cannot become an unbounded native wait.
    /// </remarks>
    public TimeSpan AdminRequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum time a Kafka client spends establishing a broker connection.
    /// </summary>
    /// <remarks>
    /// The effective value is at least one second and never exceeds
    /// <see cref="AdminRequestTimeout"/>. The five-second default prevents librdkafka's longer
    /// connection-setup default from outliving console request timeouts when a broker is offline.
    /// </remarks>
    public TimeSpan ConnectionSetupTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the managed TCP timeout used before creating native Kafka clients for console probes.
    /// </summary>
    /// <remarks>
    /// The probe is a defensive preflight for user-entered bootstrap servers. When all endpoints
    /// fail this check, the console reports the cluster as unreachable without constructing a
    /// native Kafka client.
    /// </remarks>
    public TimeSpan BootstrapEndpointProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the maximum time the native Kafka producer waits while flushing on disposal.
    /// </summary>
    public TimeSpan ProducerFlushTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the delay between native Kafka transport recovery attempts after an error.
    /// Processing retries use ModuleEventBusOption.MessageRetryCount and run within the delivery.
    /// </summary>
    public TimeSpan ConsumerErrorBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum number of consumer groups inspected by one console request.
    /// </summary>
    /// <remarks>
    /// This limit bounds both the group list returned to the UI and the number of temporary
    /// consumers created while collecting committed offsets for one performance snapshot.
    /// </remarks>
    public int MaxConsumerGroupsToInspect { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of topic partitions included in one Kafka offset request.
    /// </summary>
    /// <remarks>
    /// Large clusters can exceed broker or client request limits when all partitions are sent in
    /// one call. Batching keeps topic inventory and live performance sampling bounded.
    /// </remarks>
    public int OffsetQueryBatchSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum number of read-only consumer offset queries executed concurrently.
    /// </summary>
    /// <remarks>
    /// Each query owns one short-lived native consumer because a Kafka consumer group id is fixed
    /// when the client is built. Keep this value low to bound native memory, connections, and
    /// worker threads while sampling several groups.
    /// </remarks>
    public int ConsumerOffsetQueryParallelism { get; set; } = 2;

    /// <summary>
    /// Gets or sets the maximum number of topic configuration resources sent to one describe call.
    /// </summary>
    public int TopicConfigQueryBatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum number of topic messages a console preview request can return.
    /// </summary>
    /// <remarks>
    /// The UI can request a smaller value, but larger values are clamped to this limit to keep
    /// diagnostic reads bounded and avoid loading too many payloads into memory.
    /// </remarks>
    public int MessagePreviewMaxMessages { get; set; } = 50;

    /// <summary>
    /// Gets or sets the maximum time spent polling Kafka for one topic message preview.
    /// </summary>
    /// <remarks>
    /// Preview reads use a temporary read-only consumer and stop when this timeout is reached even
    /// if fewer than the requested number of messages were found.
    /// </remarks>
    public TimeSpan MessagePreviewTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum number of value bytes decoded for each previewed message.
    /// </summary>
    /// <remarks>
    /// Larger values are truncated before UTF-8 or base64 conversion so the console can inspect
    /// large topics without rendering unbounded payloads.
    /// </remarks>
    public int MessagePreviewMaxValueBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Adds or replaces a configured cluster using its normalized cluster id.
    /// </summary>
    /// <param name="cluster">Cluster configuration to store in the Option.</param>
    public void AddOrReplaceConfiguredCluster(KafkaClusterConfig cluster)
    {
        var normalized = cluster.Clone().Normalize();
        ConfiguredClusters.RemoveAll(existing =>
            string.Equals(existing.ClusterId, normalized.ClusterId, StringComparison.Ordinal));
        ConfiguredClusters.Add(normalized);
    }
}
