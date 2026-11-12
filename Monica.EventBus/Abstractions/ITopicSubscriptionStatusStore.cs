using Monica.EventBus.Models;

namespace Monica.EventBus.Abstractions;

/// <summary>
/// Process-wide store for the runtime health of external topic subscriptions managed by
/// distributed event bus providers (for example Dapr streaming subscriptions or Kafka consumer loops).
/// Providers report state transitions and message-level errors; monitoring surfaces consume
/// the snapshots and the <see cref="StatusChanged"/> notification.
/// The store granularity is (serviceKey, topicName). Providers with independent workload-owner
/// consumers aggregate their states here; a healthy owner must not hide a failed or recovering owner.
/// All members are thread-safe.
/// </summary>
public interface ITopicSubscriptionStatusStore
{
    /// <summary>
    /// Raised after a status snapshot changed (state transition or error report).
    /// Handlers run on the reporting thread and must not throw; the store swallows and
    /// ignores handler exceptions so reporting is never disrupted.
    /// </summary>
    event Action<TopicSubscriptionStatus>? StatusChanged;

    /// <summary>
    /// Reports a runtime state transition for a topic subscription and updates derived counters:
    /// entering <see cref="TopicSubscriptionRuntimeState.Recovering"/> increments
    /// <see cref="TopicSubscriptionStatus.ConsecutiveFailures"/> and
    /// <see cref="TopicSubscriptionStatus.RecoveryCount"/>; entering
    /// <see cref="TopicSubscriptionRuntimeState.Healthy"/> resets consecutive failures.
    /// When <paramref name="exception"/> is provided it replaces the last recorded error.
    /// </summary>
    /// <param name="serviceKey">Service key of the reporting bus instance, or <see langword="null"/> for the default instance.</param>
    /// <param name="topicName">The topic the external subscription is bound to.</param>
    /// <param name="provider">The provider kind reporting the transition.</param>
    /// <param name="state">The runtime state being entered.</param>
    /// <param name="providerMessage">Optional human-readable context recorded with the transition.</param>
    /// <param name="exception">Optional exception that caused the transition.</param>
    void ReportState(
        string? serviceKey,
        string topicName,
        EventBusProviderKind provider,
        TopicSubscriptionRuntimeState state,
        string? providerMessage = null,
        Exception? exception = null);

    /// <summary>
    /// Reports a message-level error (deserialization failure, handler failure, deadline) for a topic
    /// without changing its runtime state. Updates the last-error fields,
    /// <see cref="TopicSubscriptionStatus.MessageErrorCount"/>, and the bounded recent-error history.
    /// Creates the status entry with <see cref="TopicSubscriptionRuntimeState.Subscribing"/>
    /// if the topic has not been reported before.
    /// </summary>
    /// <param name="serviceKey">Service key of the reporting bus instance, or <see langword="null"/> for the default instance.</param>
    /// <param name="topicName">The topic the error occurred on.</param>
    /// <param name="message">Human-readable error description.</param>
    /// <param name="exception">Optional exception associated with the error.</param>
    void ReportError(string? serviceKey, string topicName, string message, Exception? exception = null);

    /// <summary>
    /// Records one successfully handled message on a topic: increments
    /// <see cref="TopicSubscriptionStatus.ProcessedMessages"/> and refreshes
    /// <see cref="TopicSubscriptionStatus.LastMessageReceivedAt"/>.
    /// This is the per-message hot path: it uses a lock-free increment, allocates nothing, and
    /// deliberately does <b>not</b> raise <see cref="StatusChanged"/> or produce a snapshot, so
    /// high-throughput topics never trigger change-notification storms. Counter changes become
    /// visible to consumers with the next snapshot produced by any other report or read.
    /// Creates the status entry with <see cref="TopicSubscriptionRuntimeState.Subscribing"/>
    /// if the topic has not been reported before.
    /// </summary>
    /// <param name="serviceKey">Service key of the reporting bus instance, or <see langword="null"/> for the default instance.</param>
    /// <param name="topicName">The topic the message was handled on.</param>
    void ReportMessageProcessed(string? serviceKey, string topicName);

    /// <summary>
    /// Gets immutable snapshots of all known topic subscriptions. Returns an empty list when no
    /// distributed provider has reported yet.
    /// </summary>
    IReadOnlyList<TopicSubscriptionStatus> GetAll();

    /// <summary>
    /// Gets the immutable snapshot for the given (serviceKey, topicName), or <see langword="null"/>
    /// when the topic is unknown to the store.
    /// </summary>
    TopicSubscriptionStatus? Get(string? serviceKey, string topicName);
}
