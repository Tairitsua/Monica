using Dapr.Client;
using System.Buffers;
using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Services.Support;
using Monica.EventBus.Models;

namespace Monica.Dapr.Services;

/// <summary>
/// Dapr-based distributed event bus implementation.
/// Publishes events using Dapr PubSub component.
/// </summary>
/// <param name="serviceScopeFactory">Creates scopes for event handlers.</param>
/// <param name="eventHandlerInvoker">Invokes resolved event handlers.</param>
/// <param name="subscriptionManager">Owns this host's subscription catalog.</param>
/// <param name="daprClient">Publishes events through the Dapr sidecar.</param>
/// <param name="messageFactory">Captures stable metadata and the JSON payload.</param>
/// <param name="daprOptions">Provides Dapr event bus options.</param>
/// <param name="loggerFactory">Creates the event bus logger.</param>
/// <param name="serviceKey">An optional keyed-provider identifier.</param>
public class DaprEventBusProvider(
    IServiceScopeFactory serviceScopeFactory,
    IEventHandlerInvoker eventHandlerInvoker,
    IEventSubscriptionRegistry subscriptionManager,
    DaprClient daprClient,
    IEventMessageFactory messageFactory,
    IOptions<ModuleDaprEventBusOption> daprOptions,
    ILoggerFactory loggerFactory,
    string? serviceKey = null)
    : DistributedEventBusBase(serviceScopeFactory, eventHandlerInvoker, subscriptionManager, loggerFactory, serviceKey), IEventBatchTransport
{
    private readonly DaprClient _daprClient = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
    private readonly ModuleDaprEventBusOption _daprOptions = daprOptions.Value ?? throw new ArgumentNullException(nameof(daprOptions));

    /// <inheritdoc />
    public EventBatchLimits BatchLimits { get; } = daprOptions.Value.PublishBatch
        ?? throw new ArgumentException("Dapr publish batch limits are required.", nameof(daprOptions));

    /// <inheritdoc />
    public async Task SendAsync(EventMessage message, CancellationToken cancellationToken)
    {
        var envelope = PrepareEnvelope(message);
        await _daprClient.PublishByteEventAsync(
            _daprOptions.PubSubName, message.Metadata.TopicName, envelope,
            "application/cloudevents+json", cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EventSendResult>> SendBatchAsync(IReadOnlyList<EventMessage> messages,
        CancellationToken cancellationToken)
    {
        BatchLimits.Validate(messages);
        var events = messages.Select(message => new PreparedCloudEvent(PrepareEnvelope(message))).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (events.Length == 0) return [];
        try
        {
            var response = await _daprClient.BulkPublishEventAsync(_daprOptions.PubSubName,
                messages[0].Metadata.TopicName, events, cancellationToken: cancellationToken);
            var results = Enumerable.Repeat(EventSendResult.Accepted, events.Length).ToArray();
            var seen = new HashSet<int>();
            foreach (var failure in response.FailedEntries)
            {
                // The pinned SDK assigns decimal input indexes as request entry IDs. CloudEvent IDs remain unchanged.
                if (!int.TryParse(failure.Entry.EntryId, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0 || index >= events.Length || !seen.Add(index))
                    throw new InvalidOperationException("Dapr returned an invalid or duplicate bulk publication entry ID.");
                results[index] = EventSendResult.Failed(new InvalidOperationException(failure.ErrorMessage));
            }
            return results;
        }
        catch (Exception exception)
        {
            // A lost or malformed response cannot prove acceptance of any entry; never retry through single sends here.
            return Enumerable.Repeat(EventSendResult.Unknown(exception), events.Length).ToArray();
        }
    }

    private ReadOnlyMemory<byte> PrepareEnvelope(EventMessage message)
    {
        if (message.Scope != EventSubscriptionScope.Distributed || message.Metadata.ServiceKey != ServiceKey)
        {
            throw new InvalidOperationException("The prepared event does not target this Dapr transport.");
        }

        var metadata = message.Metadata;
        if (string.IsNullOrWhiteSpace(metadata.MessageId) || string.IsNullOrWhiteSpace(metadata.Source))
        {
            throw new InvalidOperationException("A durable Dapr event requires a stable ID and source.");
        }

        // Dapr preserves an explicitly supplied CloudEvent envelope when sent with its
        // CloudEvents content type. The data is the original captured JSON value.
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("specversion", "1.0");
            writer.WriteString("id", metadata.MessageId);
            writer.WriteString("source", metadata.Source);
            writer.WriteString("type", metadata.EventName);
            writer.WriteString("datacontenttype", "application/json");
            if (metadata.TraceParent is not null) writer.WriteString("traceparent", metadata.TraceParent);
            if (metadata.TraceState is not null) writer.WriteString("tracestate", metadata.TraceState);
            writer.WritePropertyName("data");
            writer.WriteRawValue(message.Body.Span);
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    /// <summary>
    /// Publishes an event to Dapr PubSub.
    /// </summary>
    public override async Task PublishAsync(Type eventType, object eventData, string? topicName = null, CancellationToken cancellationToken = default)
    {
        await SendAsync(messageFactory.Prepare(eventType, eventData,
            EventSubscriptionScope.Distributed, topicName, ServiceKey), cancellationToken);
    }

    /// <summary>
    /// Publishes multiple events in bulk to Dapr PubSub.
    /// </summary>
    public override async Task BulkPublishAsync(Type eventType, IEnumerable<object> eventDataList, string? topicName = null, CancellationToken cancellationToken = default)
    {
        var messages = eventDataList.Select(eventData => messageFactory.Prepare(
            eventType, eventData, EventSubscriptionScope.Distributed, topicName, ServiceKey)).ToArray();
        await EventTransportBatch.PublishAsync(this, messages, cancellationToken);
    }
}

// Dapr 1.18 selects the CloudEvents content type only for CloudEvent instances. Its base type lacks ID,
// specversion and trace fields, so preserve the complete captured envelope through a write-only converter.
[JsonConverter(typeof(PreparedCloudEventConverter))]
internal sealed class PreparedCloudEvent(ReadOnlyMemory<byte> envelope) : global::Dapr.CloudEvent
{
    internal ReadOnlyMemory<byte> Envelope { get; } = envelope;
}

internal sealed class PreparedCloudEventConverter : JsonConverter<PreparedCloudEvent>
{
    public override PreparedCloudEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("Prepared CloudEvents are send-only snapshots.");

    public override void Write(Utf8JsonWriter writer, PreparedCloudEvent value, JsonSerializerOptions options)
        => writer.WriteRawValue(value.Envelope.Span);
}
