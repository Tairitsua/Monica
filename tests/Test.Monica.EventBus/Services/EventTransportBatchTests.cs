using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.EventBus.Services.Support;
using Xunit;

namespace Test.Monica.EventBus.Services;

public sealed class EventTransportBatchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublishAsync_WhenRoutesAndByteBudgetsChange_ShouldSendBoundedGroupsInInputOrder()
    {
        var transport = new Transport(new EventBatchLimits(3, 50));
        var messages = new[]
        {
            Message("1"), Message("2"), Message("3"), Message("4", "other"),
            Message("5", "other"), Message("6", body: new byte[100]), Message("7")
        };

        await EventTransportBatch.PublishAsync(transport, messages, Token);

        Assert.Equal([2, 1, 2, 1, 1], transport.Groups.Select(group => group.Length));
        Assert.Equal(messages, transport.Groups.SelectMany(group => group));
        Assert.Equal(["3", "6", "7"], transport.Singles.Select(message => message.Metadata.MessageId));
    }

    [Fact]
    public async Task PublishAsync_WhenOneGroupPartiallyFails_ShouldPreserveErrorAndStopLaterAdmission()
    {
        var error = new IOException("No acknowledgment");
        var transport = new Transport(new EventBatchLimits(2))
        {
            Results = [EventSendResult.Accepted, EventSendResult.Unknown(error)]
        };

        var exception = await Assert.ThrowsAsync<AggregateException>(() => EventTransportBatch.PublishAsync(
            transport, [Message("1"), Message("2"), Message("3")], Token));

        Assert.Same(error, Assert.Single(exception.InnerExceptions));
        Assert.Single(transport.Groups);
        Assert.Empty(transport.Singles);
    }

    [Fact]
    public async Task PublishAsync_WhenProviderOmitsResults_ShouldFailInsteadOfReportingSuccess()
    {
        var transport = new Transport(new EventBatchLimits()) { Results = [EventSendResult.Accepted] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => EventTransportBatch.PublishAsync(
            transport, [Message("1"), Message("2")], Token));
    }

    private static EventMessage Message(string id, string topic = "topic", byte[]? body = null) => new(
        new EventDeliveryMetadata(id, "source", "event", topic, null, null, null),
        EventSubscriptionScope.Distributed, body ?? "{}"u8.ToArray());

    private sealed class Transport(EventBatchLimits limits) : IEventBatchTransport
    {
        public EventBatchLimits BatchLimits => limits;
        public IReadOnlyList<EventSendResult>? Results { get; init; }
        public List<EventMessage[]> Groups { get; } = [];
        public List<EventMessage> Singles { get; } = [];
        public Task SendAsync(EventMessage message, CancellationToken cancellationToken)
        {
            Singles.Add(message);
            Groups.Add([message]);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<EventSendResult>> SendBatchAsync(IReadOnlyList<EventMessage> messages, CancellationToken cancellationToken)
        {
            BatchLimits.Validate(messages);
            Groups.Add(messages.ToArray());
            return Task.FromResult(Results ?? messages.Select(_ => EventSendResult.Accepted).ToArray());
        }
    }
}
