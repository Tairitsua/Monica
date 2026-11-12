using Monica.EventBus.Abstractions.Handlers;
using Monica.EventBus.Annotations;

namespace Monica.EventBus.Models.Internal;

/// <summary>
/// Represents a registered event handler with precomputed metadata.
/// </summary>
public sealed record EventHandlerRegistration
{
    public Type HandlerType { get; }
    public Type EventType { get; }
    public string TopicName { get; }
    public bool IsDistributed { get; }
    public bool IsLocal { get; }
    /// <summary>
    /// Gets a value indicating whether the registration was created automatically.
    /// </summary>
    public bool IsAutoRegistered { get; }

    public EventHandlerRegistration(
        Type handlerType,
        Type eventType,
        string topicName,
        bool isDistributed,
        bool isLocal,
        bool isAutoRegistered = false)
    {
        HandlerType = handlerType ?? throw new ArgumentNullException(nameof(handlerType));
        EventType = eventType ?? throw new ArgumentNullException(nameof(eventType));
        TopicName = topicName ?? throw new ArgumentNullException(nameof(topicName));

        // Validation: exactly one scope must be true
        if (isDistributed == isLocal)
        {
            throw new InvalidOperationException(
                $"Handler '{handlerType.FullName}' must implement either " +
                $"IDistributedEventHandler<{eventType.Name}> OR " +
                $"ILocalEventHandler<{eventType.Name}>, but not both for the same event type.");
        }

        IsDistributed = isDistributed;
        IsLocal = isLocal;
        IsAutoRegistered = isAutoRegistered;
    }

    /// <summary>
    /// Creates registration entries for the specified handler type by detecting its implemented
    /// handler interfaces automatically.
    /// </summary>
    public static IEnumerable<EventHandlerRegistration> CreateFromHandlerType(Type handlerType)
    {
        // Open handler templates cannot be activated. Their closed forms may be subscribed explicitly.
        if (handlerType.ContainsGenericParameters) return [];
        var results = new List<EventHandlerRegistration>();
        var distributedEvents = new HashSet<Type>();
        var batchEvents = new HashSet<Type>();
        var localEvents = new HashSet<Type>();

        foreach (var @interface in handlerType.GetInterfaces().Where(p => p.IsGenericType))
        {
            var eventType = @interface.GetGenericArguments()[0];
            if (eventType.IsGenericParameter) continue; // Skip open generic event type parameters.
            if (@interface.GetGenericTypeDefinition() == typeof(IDistributedEventHandler<>))
            {
                distributedEvents.Add(eventType);
            }
            else if (@interface.GetGenericTypeDefinition() == typeof(ILocalEventHandler<>))
            {
                localEvents.Add(eventType);
            }
            else if (@interface.GetGenericTypeDefinition() == typeof(IDistributedBatchEventHandler<>))
            {
                batchEvents.Add(eventType);
            }
        }

        if (batchEvents.Overlaps(distributedEvents) || batchEvents.Overlaps(localEvents))
            throw new InvalidOperationException($"Handler '{handlerType.FullName}' must choose one delivery contract per event: local, distributed single or distributed batch.");
        distributedEvents.UnionWith(batchEvents);

        // Validate no overlap
        var overlap = distributedEvents.Intersect(localEvents).ToList();
        if (overlap.Count != 0)
        {
            throw new InvalidOperationException(
                $"Handler '{handlerType.FullName}' implements both IDistributedEventHandler and " +
                $"ILocalEventHandler for the same event type(s): {string.Join(", ", overlap.Select(t => t.Name))}. " +
                $"A handler cannot process the same event type through both distributed and local buses.");
        }

        // Create registrations for distributed handlers
        foreach (var eventType in distributedEvents)
        {
            var topicName = EventNameAttribute.GetNameOrDefault(eventType);
            results.Add(new EventHandlerRegistration(
                handlerType, eventType, topicName,
                isDistributed: true, isLocal: false, isAutoRegistered: true));
        }

        // Create registrations for local handlers
        foreach (var eventType in localEvents)
        {
            var topicName = EventNameAttribute.GetNameOrDefault(eventType);
            results.Add(new EventHandlerRegistration(
                handlerType, eventType, topicName,
                isDistributed: false, isLocal: true, isAutoRegistered: true));
        }

        return results;
    }
}
