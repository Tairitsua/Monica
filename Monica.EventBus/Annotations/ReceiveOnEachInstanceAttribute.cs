namespace Monica.EventBus.Annotations;

/// <summary>Receives distributed events on each online host instance that owns this handler.</summary>
/// <remarks>Native Kafka starts a new host at the current end. Recreated consumers on that host
/// resume its committed offsets. Use this for process-local effects such as connected clients.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ReceiveOnEachInstanceAttribute : Attribute;
