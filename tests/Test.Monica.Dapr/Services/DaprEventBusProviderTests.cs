using System.Text;
using System.Text.Json;
using Dapr.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Dapr.Services;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Monica.Modules;
using NSubstitute;
using Xunit;

namespace Test.Monica.Dapr.Services;

public sealed class DaprEventBusProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SendBatchAsync_WhenOneEntryFails_ShouldPreserveEnvelopesAndCorrelateOnlyThatFailure()
    {
        var client = Substitute.For<DaprClient>();
        PreparedCloudEvent[]? sent = null;
        client.BulkPublishEventAsync("events", "topic", Arg.Any<IReadOnlyList<PreparedCloudEvent>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<IReadOnlyList<PreparedCloudEvent>>(2).ToArray();
                return Task.FromResult(new BulkPublishResponse<PreparedCloudEvent>([
                    new(new BulkPublishEntry<PreparedCloudEvent>("1", sent[1], "application/cloudevents+json"), "Broker rejected entry")
                ]));
            });
        var provider = Create(client);
        var messages = new[] { Prepared("first"), Prepared("second") };

        var results = await provider.SendBatchAsync(messages, Token);

        Assert.Equal([EventSendStatus.Accepted, EventSendStatus.Failed], results.Select(result => result.Status));
        Assert.Contains("Broker rejected entry", results[1].Error!.Message);
        Assert.NotNull(sent);
        for (var index = 0; index < sent.Length; index++)
        {
            Assert.IsAssignableFrom<global::Dapr.CloudEvent>(sent[index]);
            // Exercise the SDK's exact generic serializer path, including customized serializer options.
            var json = JsonSerializer.SerializeToUtf8Bytes(sent[index], new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(sent[index].Envelope.ToArray(), json);
            using var envelope = JsonDocument.Parse(json);
            var root = envelope.RootElement;
            Assert.Equal(messages[index].Metadata.MessageId, root.GetProperty("id").GetString());
            Assert.Equal(messages[index].Metadata.Source, root.GetProperty("source").GetString());
            Assert.Equal(messages[index].Metadata.EventName, root.GetProperty("type").GetString());
            Assert.Equal(messages[index].Metadata.TraceParent, root.GetProperty("traceparent").GetString());
            Assert.Equal(messages[index].Metadata.TraceState, root.GetProperty("tracestate").GetString());
            Assert.Equal(Encoding.UTF8.GetString(messages[index].Body.Span), root.GetProperty("data").GetRawText());
        }
        Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name == "PublishByteEventAsync");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendBatchAsync_WhenResponseIsMissingOrMalformed_ShouldLeaveEveryOutcomeUnknown(bool malformed)
    {
        var client = Substitute.For<DaprClient>();
        client.BulkPublishEventAsync("events", "topic", Arg.Any<IReadOnlyList<PreparedCloudEvent>>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call => malformed
                ? Task.FromResult(new BulkPublishResponse<PreparedCloudEvent>([
                    new(new BulkPublishEntry<PreparedCloudEvent>("99", call.ArgAt<IReadOnlyList<PreparedCloudEvent>>(2)[0],
                        "application/cloudevents+json"), "Unknown entry")
                ]))
                : Task.FromException<BulkPublishResponse<PreparedCloudEvent>>(new IOException("Response lost")));

        var results = await Create(client).SendBatchAsync([Prepared("one"), Prepared("two")], Token);

        Assert.All(results, result => Assert.Equal(EventSendStatus.Unknown, result.Status));
        Assert.DoesNotContain(client.ReceivedCalls(), call => call.GetMethodInfo().Name == "PublishByteEventAsync");
    }

    [Fact]
    public async Task SendBatchAsync_WhenScopeKeyTopicOrSizeIsInvalid_ShouldRejectBeforePublishing()
    {
        var client = Substitute.For<DaprClient>();
        var provider = Create(client);
        var first = Prepared("one");
        foreach (var invalid in new[]
                 {
                     first with { Scope = EventSubscriptionScope.Local },
                     first with { Metadata = first.Metadata with { ServiceKey = "other" } },
                     first with { Metadata = first.Metadata with { TopicName = "other" } }
                 })
            await Assert.ThrowsAsync<ArgumentException>(() => provider.SendBatchAsync([first, invalid], Token));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Create(client, new EventBatchLimits(maxBytes: 1)).SendBatchAsync([first], Token));
        Assert.Empty(await provider.SendBatchAsync([], Token));
        Assert.Empty(client.ReceivedCalls());
    }

    private static DaprEventBusProvider Create(DaprClient client, EventBatchLimits? limits = null) => new(
        Substitute.For<IServiceScopeFactory>(), Substitute.For<IEventHandlerInvoker>(),
        Substitute.For<IEventSubscriptionRegistry>(), client, Substitute.For<IEventMessageFactory>(),
        Options.Create(new ModuleDaprEventBusOption { PubSubName = "events", PublishBatch = limits ?? new() }),
        NullLoggerFactory.Instance);

    private static EventMessage Prepared(string id) => new(
        new EventDeliveryMetadata(id, "urn:tests", "changed.v1", "topic", null,
            "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "tenant=tests"),
        EventSubscriptionScope.Distributed, "{ \"value\":\"航班\", \"n\":1 }"u8.ToArray());
}
