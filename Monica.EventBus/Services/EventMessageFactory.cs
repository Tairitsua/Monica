using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;

namespace Monica.EventBus.Services;

internal sealed class EventMessageFactory(
    IJsonSerializerOptionsProvider jsonOptions,
    IHostEnvironment hostEnvironment) : IEventMessageFactory
{
    private readonly ConcurrentDictionary<string, Type> _durableContracts = new(StringComparer.Ordinal);

    public EventMessage Prepare(Type eventType, object eventData, EventSubscriptionScope scope,
        string? topicName = null, string? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(eventData);
        if (eventType.IsDefined(typeof(OutboxAttribute), inherit: false) &&
            eventData.GetType() != eventType)
        {
            throw new ArgumentException(
                $"The event value type '{eventData.GetType()}' must match the exact published contract '{eventType}'.",
                nameof(eventData));
        }

        var eventName = EventNameAttribute.GetNameOrDefault(eventType);
        if (eventType.IsDefined(typeof(OutboxAttribute), inherit: false) &&
            !eventType.GetCustomAttributes(inherit: false).OfType<IEventNameProvider>().Any())
        {
            throw new InvalidOperationException(
                $"Durable event '{eventType}' requires an explicit stable event name.");
        }

        if (eventType.IsDefined(typeof(OutboxAttribute), inherit: false))
        {
            var knownType = _durableContracts.GetOrAdd(eventName, eventType);
            if (knownType != eventType)
            {
                throw new InvalidOperationException(
                    $"Durable event name '{eventName}' is shared by '{knownType}' and '{eventType}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(topicName ?? eventName))
        {
            throw new InvalidOperationException($"Event '{eventType}' has an empty name or topic.");
        }

        OutboxOrder? order = null;
        if (scope == EventSubscriptionScope.Distributed && eventData is IOrderedOutboxEvent ordered)
        {
            if (!eventType.IsDefined(typeof(OutboxAttribute), inherit: false))
                throw new InvalidOperationException($"Ordered event '{eventType}' requires an Outbox contract.");
            var value = ordered.GetOutboxOrder();
            if (value.StreamId == Guid.Empty || value.Position <= 0)
                throw new InvalidOperationException($"Ordered event '{eventType}' requires a nonempty stream identity and a positive position.");
            order = value;
        }

        var activity = Activity.Current;
        var metadata = new EventDeliveryMetadata(
            Guid.CreateVersion7().ToString("D"),
            $"urn:monica:{Uri.EscapeDataString(hostEnvironment.ApplicationName)}",
            eventName,
            topicName ?? eventName,
            serviceKey,
            activity?.Id,
            activity?.TraceStateString)
        {
            TransportKey = order is { } stream ? $"{eventName}:{stream.StreamId:D}" : eventType.FullName ?? eventType.Name
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(eventData, eventType, jsonOptions.SerializerOptions);
        return new EventMessage(metadata, scope, bytes) { OutboxPosition = order?.Position };
    }
}
