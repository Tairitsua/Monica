using Monica.Kafka;

namespace Monica.EventBus.Kafka.Models;

/// <summary>Overrides exhausted record handling for one native Kafka subscription group.</summary>
/// <param name="Topic">Exact source topic.</param>
/// <param name="Action">Action after the handler's retry budget is exhausted.</param>
/// <param name="ServiceKey">Exact EventBus service key, or null for the default bus.</param>
/// <param name="WorkloadOwner">Exact workload owner, or null for the unowned group.</param>
/// <param name="DeadLetterTopic">Distinct target topic on the same cluster for dead-letter delivery.</param>
public sealed record KafkaSubscriptionFailurePolicy(string Topic, KafkaFailureAction Action,
    string? ServiceKey = null, string? WorkloadOwner = null, string? DeadLetterTopic = null);
