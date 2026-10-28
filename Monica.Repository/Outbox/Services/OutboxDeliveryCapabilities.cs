using System.Security.Cryptography;
using System.Text.Json;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Annotations;
using Monica.EventBus.Models;
using Monica.Repository.Outbox.Models;

namespace Monica.Repository.Outbox.Services;

/// <summary>Qualifies shared-store rows without constructing transports or executing handlers.</summary>
internal sealed class OutboxDeliveryCapabilities(IEventSubscriptionRegistry registry, string?[] transportKeys)
{
    internal string Capture(EventMessage message)
    {
        if (message.Scope == EventSubscriptionScope.Distributed)
        {
            if (!transportKeys.Contains(message.Metadata.ServiceKey, StringComparer.Ordinal))
                throw new InvalidOperationException("A shared Outbox requires an explicitly registered transport for this service key.");
            return TransportTarget(message.Metadata.ServiceKey);
        }
        if (message.Scope != EventSubscriptionScope.Local)
            throw new InvalidOperationException($"Unknown shared Outbox scope '{message.Scope}'.");
        var subscriptions = MatchingLocal(message);
        if (subscriptions.Any(subscription => EventNameAttribute.GetNameOrDefault(subscription.EventType) != message.Metadata.EventName))
            throw new InvalidOperationException("The local Outbox event name must match its registered event contract.");
        return LocalTarget(subscriptions) ?? throw new InvalidOperationException(
            "Shared Outbox local events require a nonempty, complete set of typed handlers for one event contract. " +
            "Delegate subscriptions cannot define a stable durable destination.");
    }

    internal string[] GetEligibleTargets()
    {
        var local = registry.GetAll().Where(subscription => subscription.Scope == EventSubscriptionScope.Local)
            .ToArray().GroupBy(subscription => (subscription.TopicName, subscription.ServiceKey))
            .Where(group => group.All(subscription => subscription.State == EventSubscriptionState.Active))
            .Select(group => LocalTarget(group.ToArray())).OfType<string>();
        return transportKeys.Select(TransportTarget).Concat(local).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal IReadOnlyCollection<EventSubscriptionId> SelectLocal(OutboxMessage row)
    {
        var subscriptions = MatchingLocal(row.ToEventMessage());
        if (row.DeliveryTarget is null || row.DeliveryTarget != LocalTarget(subscriptions)
            || subscriptions.Any(subscription => subscription.State != EventSubscriptionState.Active))
            throw new InvalidOperationException("The complete captured local Outbox destination is no longer active on this host.");
        return subscriptions.Select(subscription => subscription.Id).ToArray();
    }

    private IEventSubscription[] MatchingLocal(EventMessage message) => registry.GetAll()
        .Where(subscription => subscription.Scope == EventSubscriptionScope.Local
            && subscription.TopicName == message.Metadata.TopicName
            && subscription.ServiceKey == message.Metadata.ServiceKey).ToArray();

    private static string TransportTarget(string? key) => "D:" + Hash(new { ServiceKey = key });

    private static string? LocalTarget(IEventSubscription[] subscriptions)
    {
        if (subscriptions.Length == 0 || subscriptions.Any(subscription => subscription.HandlerType is null)
            || subscriptions.Select(subscription => subscription.EventType).Distinct().Count() != 1) return null;
        var first = subscriptions[0];
        // Do not use runtime subscription IDs or assembly versions. Preserve duplicates: two registrations
        // represent two executions, and a partial host must not acknowledge either on behalf of the other.
        var handlers = subscriptions.Select(subscription => JsonSerializer.Serialize(new
            { Type = TypeIdentity(subscription.HandlerType!), subscription.WorkloadOwner }))
            .Order(StringComparer.Ordinal).ToArray();
        return "L:" + Hash(new
        {
            EventName = EventNameAttribute.GetNameOrDefault(first.EventType),
            EventType = TypeIdentity(first.EventType), first.TopicName, first.ServiceKey, Handlers = handlers
        });
    }

    private static string TypeIdentity(Type type) => type.IsConstructedGenericType
        ? $"{TypeIdentity(type.GetGenericTypeDefinition())}<{string.Join(",", type.GenericTypeArguments.Select(TypeIdentity))}>"
        : $"{type.Assembly.GetName().Name}:{type.FullName}";
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
