using Monica.Core.Execution;

namespace Monica.EventBus;

/// <summary>
/// Stable execution points emitted for individual EventBus handler deliveries.
/// </summary>
public static class EventBusExecutionPoints
{
    /// <summary>
    /// Represents one in-process event-handler delivery.
    /// </summary>
    public static readonly ExecutionPoint LocalHandler = new("eventbus.local-handler");

    /// <summary>
    /// Represents one distributed event-handler delivery attempt.
    /// </summary>
    public static readonly ExecutionPoint DistributedHandler = new("eventbus.distributed-handler");

    /// <summary>Represents one atomic distributed batch-handler delivery attempt.</summary>
    public static readonly ExecutionPoint DistributedBatchHandler = new("eventbus.distributed-batch-handler");
}
