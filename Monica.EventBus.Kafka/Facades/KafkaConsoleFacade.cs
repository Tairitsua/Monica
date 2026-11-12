using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.EventBus.Kafka.Models;
using Monica.EventBus.Kafka.Services;

namespace Monica.EventBus.Kafka.Facades;

/// <summary>
/// Host-facing Kafka console entry point used by Minimal APIs and Blazor UI.
/// </summary>
public sealed class KafkaConsoleFacade(
    KafkaIntegrationService integrationService,
    KafkaClusterService clusterService,
    KafkaDashboardService dashboardService,
    KafkaTopicService topicService,
    KafkaConsumerGroupService consumerGroupService,
    KafkaPerformanceService performanceService)
{
    /// <summary>
    /// Gets the current Kafka EventBus integration state.
    /// </summary>
    public Task<Result<KafkaIntegrationSnapshot>> GetIntegrationAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => integrationService.GetSnapshotAsync(cancellationToken), "Failed to load Kafka integration state");
    }

    /// <summary>
    /// Gets Kafka clusters visible to the console.
    /// </summary>
    public Task<Result<IReadOnlyList<KafkaClusterSummary>>> ListClustersAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => clusterService.ListClustersAsync(cancellationToken), "Failed to load Kafka clusters");
    }

    /// <summary>
    /// Creates or updates a Kafka cluster.
    /// </summary>
    public Task<Result<KafkaClusterSummary>> UpsertClusterAsync(KafkaClusterUpsertRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return clusterService.UpsertClusterAsync(request.Cluster, cancellationToken);
        }, "Failed to save Kafka cluster");
    }

    /// <summary>
    /// Deletes a Kafka cluster saved through the console.
    /// </summary>
    public Task<Result> DeleteClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => clusterService.DeleteClusterAsync(clusterId, cancellationToken), "Kafka cluster deleted", "Failed to delete Kafka cluster");
    }

    /// <summary>
    /// Tests direct Kafka connectivity for a cluster.
    /// </summary>
    public Task<Result<KafkaClusterSummary>> TestClusterAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => clusterService.TestConnectionAsync(clusterId, cancellationToken), "Failed to test Kafka cluster");
    }

    /// <summary>
    /// Gets a dashboard aggregate.
    /// </summary>
    public Task<Result<KafkaDashboardSnapshot>> GetDashboardAsync(string? clusterId = null, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => dashboardService.GetDashboardAsync(clusterId, cancellationToken), "Failed to load Kafka dashboard");
    }

    /// <summary>
    /// Lists topics in a cluster.
    /// </summary>
    public Task<Result<IReadOnlyList<KafkaTopicSummary>>> ListTopicsAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => topicService.ListTopicsAsync(clusterId, cancellationToken), "Failed to load Kafka topics");
    }

    /// <summary>
    /// Creates a topic.
    /// </summary>
    public Task<Result> CreateTopicAsync(KafkaTopicCreateRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return topicService.CreateTopicAsync(request, cancellationToken);
        }, "Kafka topic created", "Failed to create Kafka topic");
    }

    /// <summary>
    /// Deletes a topic.
    /// </summary>
    public Task<Result> DeleteTopicAsync(string clusterId, string topicName, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => topicService.DeleteTopicAsync(clusterId, topicName, cancellationToken), "Kafka topic deleted", "Failed to delete Kafka topic");
    }

    /// <summary>
    /// Clears all retained records from every partition of a topic while keeping the topic itself.
    /// </summary>
    public Task<Result> ClearTopicMessagesAsync(
        string clusterId,
        string topicName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => topicService.ClearTopicMessagesAsync(clusterId, topicName, cancellationToken),
            "Kafka topic messages cleared",
            "Failed to clear Kafka topic messages");
    }

    /// <summary>
    /// Increases the partition count for a topic.
    /// </summary>
    public Task<Result> IncreasePartitionsAsync(KafkaTopicPartitionRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return topicService.IncreasePartitionsAsync(request, cancellationToken);
        }, "Kafka topic partitions updated", "Failed to update Kafka partitions");
    }

    /// <summary>
    /// Updates topic retention.
    /// </summary>
    public Task<Result> UpdateRetentionAsync(KafkaTopicRetentionRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return topicService.UpdateRetentionAsync(request, cancellationToken);
        }, "Kafka topic retention updated", "Failed to update Kafka retention");
    }

    /// <summary>
    /// Reads a bounded sample of recent messages from a topic.
    /// </summary>
    public Task<Result<KafkaTopicMessageBatch>> ReadTopicMessagesAsync(KafkaTopicMessagesRequest request, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return topicService.ReadMessagesAsync(request, cancellationToken);
        }, "Failed to read Kafka topic messages");
    }

    /// <summary>
    /// Gets partition-level retained-message inventory for a topic.
    /// </summary>
    public Task<Result<KafkaTopicBacklogSnapshot>> GetTopicBacklogAsync(
        string clusterId,
        string topicName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => topicService.GetTopicBacklogAsync(clusterId, topicName, cancellationToken),
            "Failed to load Kafka topic backlog");
    }

    /// <summary>
    /// Lists consumer groups.
    /// </summary>
    public Task<Result<IReadOnlyList<KafkaConsumerGroupSummary>>> ListConsumerGroupsAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => consumerGroupService.ListConsumerGroupsAsync(clusterId, cancellationToken), "Failed to load Kafka consumer groups");
    }

    /// <summary>
    /// Lists consumer groups associated with one topic.
    /// </summary>
    /// <param name="clusterId">Target cluster identifier.</param>
    /// <param name="topicName">Target topic name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing consumer groups associated with the topic.</returns>
    public Task<Result<IReadOnlyList<KafkaConsumerGroupSummary>>> ListTopicConsumerGroupsAsync(
        string clusterId,
        string topicName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => consumerGroupService.ListTopicConsumerGroupsAsync(clusterId, topicName, cancellationToken),
            "Failed to load Kafka topic consumer groups");
    }

    /// <summary>
    /// Gets live member identities and assignments for one consumer group.
    /// </summary>
    /// <param name="clusterId">Target cluster identifier.</param>
    /// <param name="groupId">Consumer group identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing live member identities and assignments.</returns>
    public Task<Result<IReadOnlyList<KafkaConsumerGroupMemberAssignment>>> GetConsumerGroupMembersAsync(
        string clusterId,
        string groupId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => consumerGroupService.GetConsumerGroupMembersAsync(clusterId, groupId, cancellationToken),
            "Failed to load Kafka consumer group members");
    }

    /// <summary>
    /// Captures topic/group/member/partition consumer metrics.
    /// </summary>
    /// <param name="clusterId">Target cluster identifier.</param>
    /// <param name="topicName">Optional topic filter.</param>
    /// <param name="groupId">Optional consumer-group filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the detailed consumer metrics snapshot.</returns>
    public Task<Result<KafkaConsumerMetricsSnapshot>> CaptureConsumerMetricsAsync(
        string clusterId,
        string? topicName = null,
        string? groupId = null,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => consumerGroupService.CaptureMetricsAsync(clusterId, topicName, groupId, cancellationToken),
            "Failed to capture Kafka consumer metrics");
    }

    /// <summary>
    /// Gets the latest performance snapshot for a cluster.
    /// </summary>
    public Task<Result<KafkaPerformanceSnapshot?>> GetLatestPerformanceAsync(
        string clusterId,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => performanceService.GetLatestAsync(clusterId, cancellationToken), "Failed to load the latest Kafka performance snapshot");
    }

    /// <summary>
    /// Captures a fresh performance snapshot.
    /// </summary>
    public Task<Result<KafkaPerformanceSnapshot>> CapturePerformanceAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(() => performanceService.CaptureAsync(clusterId, cancellationToken), "Failed to capture Kafka performance snapshot");
    }

    private static async Task<Result<T>> ExecuteAsync<T>(Func<Task<T>> action, string failurePrefix)
    {
        try
        {
            return Result.Success(await action());
        }
        catch (Exception ex)
        {
            return Result.Failure<T>($"{failurePrefix}: {ex.GetMessageRecursively()}");
        }
    }

    private static async Task<Result> ExecuteAsync(Func<Task> action, string successMessage, string failurePrefix)
    {
        try
        {
            await action();
            return Result.Success().WithMessage(successMessage);
        }
        catch (Exception ex)
        {
            return Result.Failure($"{failurePrefix}: {ex.GetMessageRecursively()}");
        }
    }
}
